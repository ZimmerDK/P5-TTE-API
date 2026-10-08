# Time Estimation API

## Overview
This API exposes a route estimation endpoint for a future travel-time model. It is designed to:
- receive route request data
- collect route metadata from OSM-compatible providers
- assemble a unified route object with provider provenance and missing fields
- transform the unified route into a model-ready feature vector
- run model inference
- compare results with benchmark ETAs
- return JSON to the client

## Current endpoints

### GET /health
Returns a simple status response.

### POST /api/routes/estimate
Estimates route travel time using the current model/service pipeline.

#### Request body
```json
{
  "source": "A",
  "destination": "B",
  "waypoints": ["C", "D"],
  "departureTime": "2026-09-24T08:30:00Z",
  "mode": "car"
}
```

#### Response body
```json
{
  "estimatedTravelTimeSeconds": 1234.56,
  "distanceMeters": 7420.0,
  "confidence": 0.82,
  "modelName": "route_time_model_v1",
  "route": {
    "source": "A",
    "destination": "B",
    "waypoints": ["C", "D"],
    "distanceMeters": 7420.0,
    "routeDurationSeconds": 1333.0,
    "missingData": ["roadType", "speedLimitKmh", "trafficSignalCount"],
    "providerData": [
      {
        "provider": "OSRM",
        "distanceMeters": 7420.0,
        "durationSeconds": 1333.0,
        "isAvailable": true
      }
    ]
  },
  "benchmarkComparison": {
    "osmSeconds": 1333.0,
    "googleSeconds": 1210.0,
    "absoluteErrorSeconds": 24.56,
    "relativeErrorPercent": 2.03
  },
  "features": {
    "roadType": "primary",
    "speedLimitKmh": 50,
    "distanceMeters": 7420.0,
    "waypointsCount": 2,
    "departureTime": "2026-09-24T08:30:00Z",
    "source": "A",
    "destination": "B",
    "missingData": false
  }
}
```

## Service boundaries
- OSM-compatible routing/geocoding is abstracted behind `IOsmService`
- `UnifiedRoute` is the canonical object passed from `RouteDataCollector` to
  feature engineering and returned as route diagnostics
- model execution is abstracted behind `IModelService`
- feature engineering is separated in `FeatureTransformer`
- benchmark comparison is isolated in `BenchmarkEvaluator`

This allows the OSM service and ML model to evolve independently.

## Running locally

From `TimeEstimationAPI`:

```powershell
dotnet run --launch-profile http
```

The local base URL is `http://localhost:5073`. The ready-to-run requests are in
`TimeEstimationAPI.http`.

## Routing provider configuration

Development uses a deterministic stub by default so local requests do not
depend on public services. To enable OSRM/Nominatim, set:

```powershell
$env:Osm__UseOsrm = "true"
dotnet run --no-launch-profile
```

The provider URLs are configured under `Osm` in `appsettings.json`. They can be
replaced later with a compatible geocoding/routing provider without changing
the API or model pipeline. Public provider usage is subject to its usage
policy and rate limits.

OSRM's public demo does not require an API key, but it is intended for
reasonable prototype use and may rate-limit or block requests. For production,
self-host OSRM or configure another compatible routing service.

Valhalla is the preferred primary provider when the ML team's Valhalla setup is
available. It does not need an API key when run locally. Start a Valhalla
service on the configured endpoint (by default `http://localhost:8002/route`)
and enable it:

```powershell
$env:Valhalla__Enabled = "true"
$env:Osm__UseOsrm = "true"
dotnet run --no-launch-profile
```

When both are enabled, Valhalla supplies the unified route and OSRM is retained
in `route.providerData` for comparison. The response also exposes
`benchmarkComparison.valhallaSeconds`, `osrmSeconds`, and separate absolute
and relative model-error fields for each provider. If Valhalla is down, the
collector records that failure and uses the first available provider instead.

### Local map extracts and Valhalla tiles

The optional `MapCache` job downloads configured `.osm.pbf` extracts outside
normal estimate requests. It is disabled by default. Add a region URL under
`MapCache:Regions`, then enable it:

```json
"MapCache": {
  "Enabled": true,
  "Directory": "map-cache",
  "UpdateIntervalHours": 168,
  "Regions": [
    {
      "Name": "denmark",
      "Url": "https://download.geofabrik.de/europe/denmark-latest.osm.pbf"
    }
  ]
}
```

Use the smallest Geofabrik extract covering the routes you need. The service
downloads a fresh file when the cached file is older than the configured
interval and writes through a temporary file first. It does not build
Valhalla tiles automatically yet: after a download, run Valhalla's tile
building workflow against the PBF and point the Valhalla instance at the
resulting tile directory. Tile building is kept out of HTTP requests because
it can take a long time and use substantial disk and memory.

Google Routes is optional. It requires a Google Cloud project, an API key, and
billing enabled. Do not commit the key; set it through configuration:

```powershell
$env:GoogleRoutes__ApiKey = "your-key"
```

When no Google key is configured, `benchmarkComparison.googleSeconds` and the
Google error fields are returned as `null`.

## Model hand-off

The API does not require the team model to be present during development.
`appsettings.json` and `appsettings.Development.json` enable a deterministic
placeholder predictor by default. It estimates duration from collected
distance, speed, congestion, traffic-signal, and waypoint features. This keeps
the complete request -> provider -> unified route -> feature vector -> estimate
pipeline testable.

When the trained ONNX model is available, copy it to the API output directory
as `best_model.onnx` (or configure `Model:ModelPath`) and set:

```powershell
$env:Model__UsePlaceholder = "false"
```

`ModelPath` can still point at `best_model.onnx` while the placeholder is on.
That file is then only a configured future model path; it is not loaded.
`UsePlaceholder: false` is what makes the API try to load the ONNX file.

The ONNX model must expose the same input contract currently detected by
`TravelTimeModelRunner`: a rank-3 `sequence` input and a four-value
`time_features` input. If the team model uses a different contract, update the
runner adapter and feature mapping together; do not silently change feature
ordering.

### Current placeholder feature assumptions

The placeholder path uses a few values that are not real provider data yet:

- `congestion` is a slowdown factor. `0` means no extra slowdown and `1` means
  up to 35% extra travel time in the fallback calculation. It is currently
  fixed at `0.5`, so it should not be described as live traffic.
- signal delay is a simple 10-second penalty per normalised signal unit.
- waypoints do not add a stop penalty; they are treated as intermediate route
  locations. Add an explicit stop-duration feature later if actual stops need
  to be modelled.
- urban, weather, and historical-speed values are rough defaults until the
  providers or the ML training pipeline supply proper values.

These assumptions are only for keeping the end-to-end flow usable. They need
to be replaced or aligned with the team's training features before treating
the estimate as a real model result.

## Tests

Run the integration tests with:

```powershell
dotnet test ..\TimeEstimationAPI.Tests\TimeEstimationAPI.Tests.csproj
```
