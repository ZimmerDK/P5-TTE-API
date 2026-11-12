# TimeEstimationAPI
ASP.NET Core web service that orchestrates route data collection from map providers (OSRM / Valhalla), enriches route segments with OpenStreetMap tags (via Overpass), and formats feature matrices for travel time prediction.

## Quickstart
Run the API locally using the .NET 10 SDK:
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
Settings can be configured in `appsettings.json` or via `.env` variables (see ml-service readme):

- **API Output**:
  - `Api:PrettyPrintJson`: Set to `false` for compact JSON responses. Defaults to `true`; the environment-variable form is `Api__PrettyPrintJson`.
- **Model Backend**:
  - `Model:PythonServiceUrl`: URL of the Python ML service that receives segment feature vectors (`http://127.0.0.1:8001` by default).
- **Route Providers**:
  - `Osm:UseOsrm`: Set to `true` to use live public OSRM and Nominatim endpoints for routing and geocoding.
  - `Osm:UseOverpass`: Set to `true` to enrich OSRM segments with road tags (speed limits, lanes, bridge/tunnel) from Overpass.
  - `Osm:OverpassApiKey`: Optional overpass.ch API key, supplied through the ignored `ml-service\\.env` file as `Osm__OverpassApiKey`.
  - `Context:Enabled`: Set to `true` to enrich route segments with spatial density context (road density, population density, built volume density) from the Context microservice. The launcher controls this with `-UseContext`.
  - `Context:BaseUrl`: Context service base URL. The shared default is in `appsettings.json`; override it in `ml-service\.env` for a different server.
  - `Context:ApiKey`: Optional API key sent as the `X-API-Key` header. Store it in the ignored `ml-service\.env` file, not in tracked configuration.
  - `Valhalla:Enabled`: Set to `true` to use a local Valhalla routing server if available (TBD — Doesn't work at all yet).
  - `GoogleMaps:Enabled`: Set to `true` to make a Google Routes API comparison call with traffic-aware and no-traffic route durations. Disabled by default.
  - `GoogleMaps:ApiKey`: Server-side Google API key, supplied through the ignored `ml-service\.env` file as `GoogleMaps__ApiKey` or through user secrets.
  - `GoogleMaps:RoutesUrl`: Google Routes API endpoint. The default is `https://routes.googleapis.com/directions/v2:computeRoutes`.

Google uses the existing OSM/Nominatim geocoding flow and does not geocode addresses itself. When enabled, its route duration and distance are returned as `googleComparison` alongside the primary provider and model estimate. 
Google comparison is opt-in and should remain disabled unless a request needs it. The API key shouldn't be committed to config or GitHub.

Requests can set `useGoogleMaps` to `false` to opt out for that one request even when the Google comparison provider is enabled globally. If omitted, the configured launcher setting is used.

Comparison deltas use `Google – comparison`: a positive value means Google estimated more seconds (i.e. slower), a negative value means Google estimated fewer seconds (i.e. faster), while zero means they matched.

---

## Key Files
- [`Program.cs`](./Program.cs:1) — Dependency injection configuration and HTTP endpoint definitions.
- [`Services/RouteProvider.cs`](./Services/RouteProvider.cs:1) — Shared route-provider contract, result model, and provider exception.
- [`Services/OsrmService.cs`](./Services/OsrmService.cs:1) — OSRM integration and OSRM response parsing.
- [`Services/ValhallaService.cs`](./Services/ValhallaService.cs:1) — Valhalla routing integration.
- [`Services/DevelopmentRouteProvider.cs`](./Services/DevelopmentRouteProvider.cs:1) — Deterministic offline route provider used when live OSRM is disabled.
- [`Services/RouteDataCollector.cs`](./Services/RouteDataCollector.cs:1) — Coordinates route queries across configured providers.
- [`Services/OsmTagEnricher.cs`](./Services/OsmTagEnricher.cs:1) — Enriches route segments using Overpass OSM queries.
- [`Services/TrafficContextEnricher.cs`](./Services/TrafficContextEnricher.cs:1) — Enriches route segments with spatial context density metrics from the configured `/context` endpoint.
- [`Services/FeatureTransformer.cs`](./Services/FeatureTransformer.cs:1) — Converts route data into feature vectors for model prediction.
- [`Services/PythonModelRunner.cs`](./Services/PythonModelRunner.cs:1) — HTTP client connecting to the FastAPI Python ML service.
