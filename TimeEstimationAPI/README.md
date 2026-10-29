# TimeEstimationAPI
ASP.NET Core web service that orchestrates route data collection from map providers (OSRM / Valhalla), enriches route segments with OpenStreetMap tags (via Overpass), and formats feature matrices for travel time prediction.

## Quickstart
Run the API locally using .NET 9 SDK:
```powershell
dotnet run --launch-profile http
```

To then hot-reload services on demand when using the launcher script:
```powershell
.\Reload-Services.ps1
```

The API will start at `http://localhost:5073`. You can test endpoints using [`TimeEstimationAPI.http`](./TimeEstimationAPI.http).

To run unit tests:
```powershell
dotnet test ..\TimeEstimationAPI.Tests\TimeEstimationAPI.Tests.csproj
```

---

## Configuration Options
Settings can be configured in `appsettings.json` or via environment variables:

- **Model Backend**:
  - `Model:UsePlaceholder`: Set to `true` for standalone development without running the Python ML service.
  - `Model:UsePythonService`: Set to `true` to send segment feature vectors to the Python ML service at `Model:PythonServiceUrl` (`http://127.0.0.1:8001`).
- **Route Providers**:
  - `Osm:UseOsrm`: Set to `true` to use live public OSRM and Nominatim endpoints for routing and geocoding.
  - `Osm:UseOverpass`: Set to `true` to enrich OSRM segments with road tags (speed limits, lanes, bridge/tunnel) from Overpass (rarely works tbh).
  - `Context:Enabled`: Set to `true` to enrich route segments with spatial density context (road density, population density, built volume density) from the Context microservice (`Context:BaseUrl` = `http://localhost:8010/`). The Spatial Context service must be started manually (e.g. via Docker Compose at `C:\Dev\spatial-context-service.v2`).
  - `Valhalla:Enabled`: Set to `true` to use a local Valhalla routing server if available (TBD — Doesn't work at all yet).

---

## Key Files
- [`Program.cs`](./Program.cs:1) — Dependency injection configuration and HTTP endpoint definitions.
- [`Services/RouteDataCollector.cs`](./Services/RouteDataCollector.cs:1) — Coordinates route queries across configured providers.
- [`Services/OsmTagEnricher.cs`](./Services/OsmTagEnricher.cs:1) — Enriches route segments using Overpass OSM queries.
- [`Services/TrafficContextEnricher.cs`](./Services/TrafficContextEnricher.cs:1) — Enriches route segments with spatial context density metrics from `http://localhost:8010/context`.
- [`Services/FeatureTransformer.cs`](./Services/FeatureTransformer.cs:1) — Converts route data into feature vectors for model prediction.
- [`Services/PythonModelRunner.cs`](./Services/PythonModelRunner.cs:1) — HTTP client connecting to the FastAPI Python ML service.
