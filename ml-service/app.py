import os
from contextlib import asynccontextmanager
from typing import Any

import joblib
import numpy as np
import pandas as pd
from fastapi import FastAPI, HTTPException
from dotenv import load_dotenv
from pydantic import AliasChoices, BaseModel, ConfigDict, Field

load_dotenv()

MODEL_FILE_NAMES = {
    "DecisionTreeRegressor": "DecisionTreeRegressor.joblib",
    "LinearRegression": "LinearRegression.joblib",
    "RandomForestRegressor": "RandomForestRegressor.joblib",
    "Ridge": "Ridge.joblib",
    "XGBRegressor": "XGBRegressor.json",
}


class PredictionRequest(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    model_name: str = Field(
        min_length=1,
        validation_alias=AliasChoices("modelName", "model_name"),
    )
    route_features: dict[str, Any] = Field(
        default_factory=dict,
        validation_alias=AliasChoices("routeFeatures", "route_features"),
    )
    segment_features: list[dict[str, Any]] = Field(
        default_factory=list,
        validation_alias=AliasChoices("segmentFeatures", "segment_features"),
    )
    time_features: dict[str, Any] = Field(
        default_factory=dict,
        validation_alias=AliasChoices("timeFeatures", "time_features"),
    )


class PredictionResponse(BaseModel):
    estimated_travel_time_seconds: float
    segment_predictions: list[float | None] = Field(default_factory=list)
    model_name: str | None = None


def load_model() -> Any:
    model_path = _model_path()
    if not os.path.isfile(model_path):
        raise RuntimeError(f"Model file does not exist: {model_path}")

    if model_path.endswith(".json"):
        import xgboost as xgb
        model = xgb.XGBRegressor()
        model.load_model(model_path)
        return model

    return joblib.load(model_path)


def load_model_config() -> dict[str, Any] | None:
    """
    The training pipeline saves <model>_config.json next to <model>.json. It says how the model
    output becomes seconds (target transform, time-of-day factor). Older models have none and
    already predict seconds.
    """
    config_path = os.path.splitext(_model_path())[0] + "_config.json"
    if not os.path.isfile(config_path):
        return None
    import json
    with open(config_path, "r", encoding="utf-8") as file:
        return json.load(file)


@asynccontextmanager
async def lifespan(application: FastAPI):
    application.state.model = load_model()
    application.state.model_config = load_model_config()
    yield


app = FastAPI(title="Time Estimation ML Service", version="1.0", lifespan=lifespan)


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok"}


@app.get("/model-info")
def model_info() -> dict[str, Any]:
    model = getattr(app.state, "model", None)
    if model is None:
        raise HTTPException(status_code=503, detail="Model is not loaded.")

    feature_names = _get_model_feature_names(model)
    return {
        "modelPath": _model_path(),
        "modelType": type(model).__name__,
        "inputMode": os.environ.get("MODEL_INPUT_MODE", "segments"),
        "configuredFeatures": _configured_feature_names(),
        "modelFeatures": feature_names,
        "targetTransform": (getattr(app.state, "model_config", None) or {}).get("target_transform"),
        "hasTimeFactor": (getattr(app.state, "model_config", None) or {}).get("time_factor") is not None,
    }


@app.post("/predict", response_model=PredictionResponse)
def predict(request: PredictionRequest) -> PredictionResponse:
    model = getattr(app.state, "model", None)
    if model is None:
        raise HTTPException(status_code=503, detail="Model is not loaded.")
    if not request.segment_features:
        raise HTTPException(status_code=400, detail="At least one segment is required.")

    try:
        predictions = _predict(model, request)
        model_config = getattr(app.state, "model_config", None)
        if model_config is not None:
            predictions = _to_seconds(np.asarray(predictions, dtype=float), request.segment_features, model_config)
    except (TypeError, ValueError, KeyError) as exception:
        raise HTTPException(status_code=422, detail=f"Model input was rejected: {exception}") from exception

    values = np.asarray(predictions, dtype=float).reshape(-1)
    if values.size == 0 or not np.all(np.isfinite(values)):
        raise HTTPException(status_code=500, detail="Model returned no finite prediction.")

    segment_predictions = values.tolist() if values.size == len(request.segment_features) else []
    total = float(values.sum()) if segment_predictions else float(values[0])
    model_name = os.path.splitext(os.path.basename(_model_path()))[0]
    return PredictionResponse(
        estimated_travel_time_seconds=total,
        segment_predictions=segment_predictions,
        model_name=model_name,
    )


def _predict(model: Any, request: PredictionRequest) -> Any:
    input_mode = os.environ.get("MODEL_INPUT_MODE", "segments").lower()
    feature_names = _get_model_feature_names(model)

    if input_mode == "segments":
        rows = [
            _select_features(segment, feature_names)
            for segment in request.segment_features
        ]
        model_input = pd.DataFrame(rows)
    elif input_mode == "route":
        row = {
            **request.route_features,
            **request.time_features,
            "segmentFeatures": request.segment_features,
        }
        model_input = pd.DataFrame([_select_features(row, feature_names)])
    else:
        raise ValueError("MODEL_INPUT_MODE must be 'route' or 'segments'.")

    return model.predict(model_input)


def _to_seconds(values: np.ndarray, segments: list[dict[str, Any]], config: dict[str, Any]) -> np.ndarray:
    """
    Turns the model output into seconds per segment, as described in the model's config:
    undo the target transform, then multiply by the time-of-day factor for the segment's
    weekday/weekend and hour. Mirrors predictSeconds/applyTimeFactor in aSTEP_MLM/scripts/MLMscript.py.
    """
    if os.environ.get("MODEL_INPUT_MODE", "segments").lower() != "segments":
        raise ValueError("A model with a target transform or time factor needs MODEL_INPUT_MODE=segments.")

    transform = config.get("target_transform")
    if transform == "log_time":
        values = np.exp(values)
    elif transform == "log_speed":
        length_feature = config["length_feature"]
        lengths = np.array([float(segment[length_feature]) for segment in segments])
        values = np.maximum(lengths, config["min_length_m"]) / np.exp(values)
    elif transform is not None:
        raise ValueError(f"Unknown target transform in the model config: {transform}")

    factor = config.get("time_factor")
    if factor is not None:
        hours = [int(segment["hour_enter_time"]) for segment in segments]
        weekends = [bool(segment.get("weekend_enter_time", 0)) for segment in segments]
        values = values * np.array([
            factor["weekend" if weekend else "weekday"][hour]
            for hour, weekend in zip(hours, weekends)
        ])

    return values


def _get_model_feature_names(model: Any) -> list[str] | None:
    configured = os.environ.get("MODEL_FEATURES", "").strip()
    if configured:
        return [name.strip() for name in configured.split(",") if name.strip()]

    feature_names = getattr(model, "feature_names_in_", None)
    if feature_names is None and hasattr(model, "get_booster"):
        try:
            feature_names = getattr(model.get_booster(), "feature_names", None)
        except Exception:
            pass

    if feature_names is not None:
        return list(feature_names)

    return _configured_feature_names()


def _configured_feature_names() -> list[str] | None:
    configured = os.environ.get("MODEL_FEATURES", "").strip()
    if configured:
        return [name.strip() for name in configured.split(",") if name.strip()]

    # Check for feature_order.json in model folder if present
    model_dir = os.environ.get("MODEL_DIR", "models")
    order_file = os.path.join(model_dir, "feature_order.json")
    if os.path.isfile(order_file):
        try:
            import json
            with open(order_file, "r", encoding="utf-8") as f:
                data = json.load(f)
                if isinstance(data, dict) and "features" in data:
                    return list(data["features"])
                if isinstance(data, list):
                    return data
        except Exception:
            pass

    return None


def _model_path() -> str:
    configured_path = os.environ.get("MODEL_PATH", "").strip()
    if configured_path:
        return configured_path

    model_name = os.environ.get("MODEL_NAME", "XGBRegressor").strip()
    try:
        file_name = MODEL_FILE_NAMES[model_name]
    except KeyError as exception:
        supported = ", ".join(MODEL_FILE_NAMES)
        raise RuntimeError(
            f"Unsupported MODEL_NAME '{model_name}'. Choose one of: {supported}."
        ) from exception

    model_directory = os.environ.get("MODEL_DIR", "models")
    return os.path.join(model_directory, file_name)


def _select_features(row: dict[str, Any], feature_names: list[str] | None) -> dict[str, Any]:
    if feature_names is None:
        return row
    return {name: row.get(name, 0) for name in feature_names}
