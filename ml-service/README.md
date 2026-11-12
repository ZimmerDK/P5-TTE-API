# ML Service
FastAPI Python service that loads trained machine learning model artifacts (`.joblib`, or XGBoost `.json`) and exposes HTTP inference endpoints for travel time estimation.

## Setup & Running
### 1. Environment Setup
```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
Copy-Item .env.example .env
```

### 2. Model Configuration
Place your trained model file into the `models/` directory (e.g. `ml-service/models/XGBRegressor.json` for XGBoost).
Configure `.env`:
```ini
MODEL_DIR=models
MODEL_NAME=XGBRegressor
```

Supported model settings include `DecisionTreeRegressor`, `LinearRegression`, `RandomForestRegressor`, `Ridge`, and `XGBRegressor`.

### 3. Start Standalone Service
```powershell
.\.venv\Scripts\python.exe -m uvicorn app:app --host 127.0.0.1 --port 8001
```

The ML service will run at `http://127.0.0.1:8001`.

---

## Endpoints
- **`GET /health`**: Health check.
- **`GET /model-info`**: Returns metadata, active feature list, and model configuration.
- **`POST /predict`**: Accepts one feature row per route segment from the ASP.NET Core API and returns segment predictions plus their total in seconds.
