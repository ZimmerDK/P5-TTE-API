# Travel Time Estimation API
A 5th-semester project API for predicting route travel times using our ML model. It combines map data and routing from OpenStreetMap(OSM) providers (OSRM / Valhalla) with machine learning regression models running in a companion Python service.

## Quickstart
### 1. First-time Setup
Create the '.env' config file and ensure that Python 3.10+ and .NET 9 SDK are installed:
```powershell
python -m venv ml-service\.venv
.\ml-service\.venv\Scripts\python.exe -m pip install -r ml-service\requirements.txt
Copy-Item ml-service\.env.example ml-service\.env
```

Place the MLM trained `.joblib` model files into `ml-service\models\` (e.g. `XGBRegressor.joblib`) and change the `MODEL_NAME` in `ml-service\.env`.

### 2. Start Services
Run the launcher script to start both the Python ML service and the ASP.NET Core API:
```powershell
.\Start-LocalServices.ps1 -UseOsrm
```

- **ASP.NET Core API**: `http://localhost:5073`
- **Python ML Service**: `http://127.0.0.1:8001`

Use the Postman collection in `postman/` or `TimeEstimationAPI/TimeEstimationAPI.http` to send test requests.

---

## Key Endpoints
| Endpoint | Method | Description |
|---|---|---|
| `/api/routes/estimate` | `POST` | Full flow: fetches route from OSRM/Valhalla, extracts road features, and predicts travel time with the ML model. |
| `/api/routes/provider-data` | `POST` | Diagnostic endpoint: returns routing geometry, timings, and OSM tag enrichment without calling the ML model. |
| `/api/routes/model-only` | `POST` | Model test endpoint: accepts explicit segment features and returns ML predictions directly. |
| `/health` | `GET` | Simple health check endpoint for liveness verification. |

### Example Request (`POST /api/routes/estimate`)
```json
{
  "source": "Aalborg Station, Denmark",
  "destination": "Karolinelundsvej 45, 9000 Aalborg, Denmark",
  "departureTime": "2026-10-27T08:30:00+01:00",
  "mode": "car"
}
```

---

## Project Structure
- [`TimeEstimationAPI/`](./TimeEstimationAPI/README.md) — ASP.NET Core web API handling routing provider integration, OSM enrichment, and feature formatting.
- [`ml-service/`](./ml-service/README.md) — FastAPI Python service that loads `.joblib` model artifacts and serves HTTP predictions.
- [`Start-LocalServices.ps1`](./Start-LocalServices.ps1) — PowerShell script to launch both services in parallel locally.
- [`postman/`](./postman/) — Postman API request collection.
