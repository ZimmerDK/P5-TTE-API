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
    "XGBRegressor": "XGBRegressor.joblib",
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
    return joblib.load(model_path)


@asynccontextmanager
async def lifespan(application: FastAPI):
    application.state.model = load_model()
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

    feature_names = getattr(model, "feature_names_in_", None)
    return {
        "modelPath": _model_path(),
        "modelType": type(model).__name__,
        "inputMode": os.environ.get("MODEL_INPUT_MODE", "segments"),
        "configuredFeatures": _configured_feature_names(),
        "modelFeatures": feature_names.tolist() if feature_names is not None else None,
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
    feature_names = _configured_feature_names()

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


def _configured_feature_names() -> list[str] | None:
    configured = os.environ.get("MODEL_FEATURES", "").strip()
    return [name.strip() for name in configured.split(",") if name.strip()] or None


def _model_path() -> str:
    configured_path = os.environ.get("MODEL_PATH", "").strip()
    if configured_path:
        return configured_path

    model_name = os.environ.get("MODEL_NAME", "RandomForestRegressor").strip()
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
    missing = [name for name in feature_names if name not in row]
    if missing:
        raise KeyError(f"Missing configured features: {', '.join(missing)}")
    return {name: row[name] for name in feature_names}
