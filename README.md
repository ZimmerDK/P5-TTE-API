# Travel Time Estimation API
A 5th-semester project API for predicting route travel times using our ML model. It combines map data and routing from OpenStreetMap(OSM) providers (OSRM / Valhalla) with machine learning regression models running in a companion Python service.

## Quickstart
### 1. First-time Setup
Create the '.env' config file and ensure that Python 3.12+ and .NET 10 SDK are installed:
```powershell
python -m venv ml-service\.venv
.\ml-service\.venv\Scripts\python.exe -m pip install -r ml-service\requirements.txt
Copy-Item ml-service\.env.example ml-service\.env
```

Place the trained ML model file (`.joblib` or `.json`) into `ml-service\models\` (e.g. `XGBRegressor.json` for XGBoost) and change the `MODEL_NAME` in `ml-service\.env`.

### 2. Start Services
#### Option A: Launcher Script (`Start-LocalServices.ps1`)
Run the launcher script to start both the Python ML service and the ASP.NET Core API:
```powershell
# Start local API & Python ML service:
.\Start-LocalServices.ps1 -UseOsrm -UseContext

# Hot reload services on command when code updated:
.\Reload-Services.ps1

# Restart only the ASP.NET Core API while keeping the ML service running:
.\Reload-Services.ps1 -ApiOnly

# Stop local services when done:
.\Reload-Services.ps1 -StopOnly
```

Launcher waits for the Python model service to be read/"healthy" before starting the ASP.NET Core API. 
Use `-ApiOnly` when you made changes to only the C# code, so that the ML service doesn't reload. 
The launcher also uses `--no-restore` for local reloads; run `dotnet restore` after changing project dependencies.

### Optional Google Routes comparison
Google Maps integration is disabled by default. 
Do not paste the API key into chat, source code, `appsettings.json`, Postman, or a committed file. Put it only in the ignored local `ml-service\.env` file:
```powershell
GoogleMaps__ApiKey=your-key
```

Then enable it explicitly with the launcher:
```powershell
.\Start-LocalServices.ps1 -UseOsrm -UseGoogle
```

The API uses existing OSM/Nominatim geocoding and sends those coordinates to Google's Routes API. 
Google is an additional comparison provider and does not replace OSRM. Response includes optional `googleComparison` metrics for whole route and each segment, including Google's no-traffic route duration. Leave `-UseGoogle` out to make no Google requests, or change the `UseGoogleMaps` value in individual json request body to `false` to opt out even when Google Maps Routes API is enabled globally:
```json
{
  "source": "Aalborg Station, Denmark",
  "destination": "Karolinelundsvej 45, 9000 Aalborg, Denmark",
  "useGoogleMaps": false
}
```
When `useGoogleMaps` is omitted, the launcher/configuration default is used. Setting it to `false` guarantees that request will not call Google.

For subsequent changes, `.\Reload-Services.ps1` preserves these switches. Use `.\Reload-Services.ps1 -ApiOnly` for C# changes without restarting the ML service.

Each user must use their own Google Cloud / Google Maps Platform API key. 
Google Cloud billing and API quotas are controlled by Google Cloud, not by this API. Enable only the Routes API, restrict the key to the Routes API and your server environment, and configure a budget/quota alert. A budget alert does NOT mean a hard spending cap, so do not enable Google if you require an absolute guarantee of zero billing.

Comparison deltas use `Google – comparison`: positive means Google estimated more seconds, while negative means Google estimated fewer seconds.

- **ASP.NET Core API**: `http://localhost:5073`
- **Python ML Service**: `http://127.0.0.1:8001`

#### Option B: MANUAL | Run Individually in VS Code Terminals
1. **ASP.NET Core API**:
   ```powershell
   dotnet run --project TimeEstimationAPI\TimeEstimationAPI.csproj --launch-profile http
   ```
2. **Python ML Service**:
   ```powershell
   .\ml-service\.venv\Scripts\python.exe -m uvicorn app:app --host 127.0.0.1 --port 8001
   ```

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
  "departureTime": "2026-10-27T14:30:00+01:00"
}
```

---

## Project Structure
- [`TimeEstimationAPI/`](./TimeEstimationAPI/README.md) — ASP.NET Core web API handling routing provider integration, OSM enrichment, and feature formatting.
- [`TimeEstimationAPI/Services/OsrmService.cs`](./TimeEstimationAPI/Services/OsrmService.cs) — OSRM integration and route-step parsing.
- [`TimeEstimationAPI/Services/ValhallaService.cs`](./TimeEstimationAPI/Services/ValhallaService.cs) — Valhalla routing integration.
- [`TimeEstimationAPI/Services/DevelopmentRouteProvider.cs`](./TimeEstimationAPI/Services/DevelopmentRouteProvider.cs) — Offline provider used for local development.
- [`ml-service/`](./ml-service/README.md) — FastAPI Python service that loads `.joblib` model artifacts and serves HTTP predictions.
- [`Start-LocalServices.ps1`](./Start-LocalServices.ps1) — PowerShell script to launch local services in parallel.
- [`Reload-Services.ps1`](./Reload-Services.ps1) — PowerShell script to hot-reload local services on demand.
- [`postman/`](./postman/) — Postman API request collection.
