# Time Estimation ML service

This service loads the ML team's Python `.joblib` artifact and exposes
prediction over HTTP. The ASP.NET API calls it when
`Model:UsePythonService` is enabled.

## Setup

From the repository root, perform the one-time setup:

```powershell
cd C:\Dev\P5-TTE-API
python -m venv .\ml-service\.venv
.\ml-service\.venv\Scripts\python.exe -m pip install -r .\ml-service\requirements.txt
Copy-Item .\ml-service\.env.example .\ml-service\.env
```

Use the Python and dependency versions required to load the model. The
constraints in `requirements.txt` currently allow Python 3.14-compatible
wheels; align them with the training environment before using the production
model.

```powershell
cd C:\Dev\P5-TTE-API\ml-service
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
```

Place the model outside source control, for example:

```text
C:\Dev\P5-TTE-API\ml-service\models\XGBRegressor.joblib
```

The repository supports the model files currently available:

- `DecisionTreeRegressor.joblib`
- `LinearRegression.joblib`
- `RandomForestRegressor.joblib`
- `Ridge.joblib`
- `XGBRegressor.joblib`

Copy [`.env.example`](./.env.example) to `.env` and select the model:

```powershell
Copy-Item .env.example .env
```

Then set `MODEL_NAME` in `.env`, for example:

```text
MODEL_DIR=models
MODEL_NAME=RandomForestRegressor
MODEL_INPUT_MODE=segments
MODEL_FEATURES=matched_length_m,hour,minute,day_of_week,month,speed_limit_kmh,density,lane_count,bridge,tunnel,roundabout,internal_intersection
```

Use `MODEL_PATH` instead when the file has a different name or is stored
elsewhere. The `.env` file is ignored by Git because it contains machine-local
paths and model settings.

Run the service:

```powershell
.\.venv\Scripts\python.exe -m uvicorn app:app --host 127.0.0.1 --port 8001
```

If imports are underlined in VS Code, select
`ml-service\.venv\Scripts\python.exe` as the Python interpreter and run the
dependency installation command above. The model file is loaded at service
startup, so the service fails early if it is missing or cannot be
deserialized.

The current supplied files expose the same 12 numeric features, in the order shown
above. The service verifies that configured features exist before prediction.
The model metadata can be inspected at `/model-info`; the `modelFeatures` field
is read from `feature_names_in_`.

`MODEL_INPUT_MODE=segments` calls `model.predict` with one row per route
segment. The returned predictions are one value per segment; the API sums them
for the total route estimate.

The API currently has no provider data for `density`, `bridge`, or `tunnel`;
those values are sent as `0` until the route providers supply them.
This is an integration assumption that should be confirmed with the ML team.

Inspect the loaded model and its discovered `feature_names_in_` values at:

```text
http://127.0.0.1:8001/model-info
```

The `.joblib` file is intentionally not included in this repository. Joblib
uses Python pickle serialization, so only load trusted model files.

The ASP.NET API setup and request examples are in
[`TimeEstimationAPI/README.md`](../TimeEstimationAPI/README.md).

## Endpoints

- `GET /health`
- `GET /model-info`
- `POST /predict`

The ASP.NET API sends a request containing `routeFeatures`,
`segmentFeatures`, and `timeFeatures`. This boundary can be adjusted once the
ML feature contract is finalized. A direct segment-mode request looks like:

```json
{
  "modelName": "route_time_model_v1",
  "routeFeatures": {},
  "segmentFeatures": [
    {
      "matched_length_m": 1000,
      "hour": 8,
      "minute": 30,
      "day_of_week": 4,
      "month": 10,
      "speed_limit_kmh": 50,
      "density": 0,
      "lane_count": 2,
      "bridge": 0,
      "tunnel": 0,
      "roundabout": 0,
      "internal_intersection": 1
    }
  ],
  "timeFeatures": {}
}
```

The response contains one prediction per input segment:

```json
{
  "estimated_travel_time_seconds": 75.1,
  "segment_predictions": [75.1]
}
```
