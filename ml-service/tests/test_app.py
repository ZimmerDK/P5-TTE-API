import os
import sys
from pathlib import Path

import numpy as np
import pytest
from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).parents[1]))
import app as service


class PredictingModel:
    feature_names_in_ = np.array(["length_m", "speed_limit_kmh"])

    def __init__(self, values):
        self.values = values
        self.received = None

    def predict(self, model_input):
        self.received = model_input
        return np.asarray(self.values)


@pytest.fixture
def client():
    test_client = TestClient(service.app)
    yield test_client
    test_client.close()


@pytest.mark.integration
def test_health_reports_service_liveness(client):
    response = client.get("/health")

    assert response.status_code == 200
    assert response.json() == {"status": "ok"}


@pytest.mark.integration
def test_predict_selects_model_features_and_sums_segment_predictions(client):
    model = PredictingModel([67.0, 6.7])
    service.app.state.model = model

    response = client.post(
        "/predict",
        json={
            "modelName": "ignored-by-service",
            "segmentFeatures": [
                {"length_m": 69, "speed_limit_kmh": 50, "unexpected": 420},
                {"length_m": 67},
            ],
        },
    )

    assert response.status_code == 200
    assert response.json() == {
        "estimated_travel_time_seconds": 73.7,
        "segment_predictions": [67.0, 6.7],
        "model_name": "XGBRegressor",
    }
    received = model.received
    assert received is not None
    assert list(received.columns) == ["length_m", "speed_limit_kmh"]
    assert received.iloc[1].to_dict() == {
        "length_m": 67,
        "speed_limit_kmh": 0,
    }


@pytest.mark.integration
def test_predict_rejects_empty_segment_features(client):
    service.app.state.model = PredictingModel([67.0])

    response = client.post("/predict", json={"segmentFeatures": []})

    assert response.status_code == 400
    assert response.json()["detail"] == "At least one segment is required."


@pytest.mark.integration
def test_predict_reports_model_input_errors_as_unprocessable_entity(client):
    class RejectingModel:
        feature_names_in_ = np.array(["length_m"])

        def predict(self, _):
            raise ValueError("invalid feature value")

    service.app.state.model = RejectingModel()

    response = client.post(
        "/predict",
        json={"segmentFeatures": [{"length_m": "not-a-number"}]},
    )

    assert response.status_code == 422
    assert "Model input was rejected: invalid feature value" in response.json()["detail"]


@pytest.mark.integration
def test_predict_rejects_non_finite_model_output(client):
    service.app.state.model = PredictingModel([np.nan])

    response = client.post(
        "/predict",
        json={"segmentFeatures": [{"length_m": 67}]},
    )

    assert response.status_code == 500
    assert response.json()["detail"] == "Model returned no finite prediction."


@pytest.mark.integration
def test_predict_returns_service_unavailable_when_model_is_not_loaded(client):
    service.app.state.model = None

    response = client.post(
        "/predict",
        json={"segmentFeatures": [{"length_m": 67}]},
    )

    assert response.status_code == 503
    assert response.json()["detail"] == "Model is not loaded."


@pytest.mark.integration
def test_prediction_request_rejects_empty_model_name(client):
    response = client.post(
        "/predict",
        json={"modelName": "", "segmentFeatures": [{"length_m": 67}]},
    )

    assert response.status_code == 422


@pytest.mark.integration
def test_model_info_reports_loaded_model_and_features(client, monkeypatch):
    service.app.state.model = PredictingModel([67.0])
    monkeypatch.setenv("MODEL_PATH", os.path.join("fixtures", "model.joblib"))

    response = client.get("/model-info")

    assert response.status_code == 200
    assert response.json()["modelType"] == "PredictingModel"
    assert response.json()["modelFeatures"] == ["length_m", "speed_limit_kmh"]


@pytest.mark.integration
def test_model_info_returns_service_unavailable_when_model_is_not_loaded(client):
    service.app.state.model = None

    response = client.get("/model-info")

    assert response.status_code == 503
    assert response.json()["detail"] == "Model is not loaded."


@pytest.mark.integration
def test_predict_uses_first_prediction_when_model_returns_one_value(client):
    service.app.state.model = PredictingModel([67.0])

    response = client.post(
        "/predict",
        json={
            "segmentFeatures": [
                {"length_m": 69},
                {"length_m": 67},
            ]
        },
    )

    assert response.status_code == 200
    assert response.json()["estimated_travel_time_seconds"] == 67.0
    assert response.json()["segment_predictions"] == []


@pytest.mark.unit
def test_select_features_fills_missing_configured_values_with_zero():
    selected = service._select_features(
        {"length_m": 67},
        ["length_m", "speed_limit_kmh"],
    )

    assert selected == {"length_m": 67, "speed_limit_kmh": 0}


@pytest.mark.unit
def test_model_path_rejects_an_unsupported_model_name(monkeypatch):
    monkeypatch.delenv("MODEL_PATH", raising=False)
    monkeypatch.setenv("MODEL_NAME", "UnsupportedModel")

    with pytest.raises(RuntimeError, match="Unsupported MODEL_NAME"):
        service._model_path()
