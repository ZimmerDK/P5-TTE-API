DISCLAIMER: This document is currently primarily AI-generated. 

# Time Estimation API
Small ASP.NET Core API that estimates a route's travel time.

## Quick start

### Run the offline API

```powershell
cd C:\Dev\P5-TTE-API
dotnet run --project .\TimeEstimationAPI\TimeEstimationAPI.csproj --launch-profile http
Invoke-RestMethod http://localhost:5073/health
```

The API is available at `http://localhost:5073`. Use
[`TimeEstimationAPI.http`](./TimeEstimationAPI.http) for ready-made requests.

### Enable the Python model service

Read the dedicated [ML service setup guide](../ml-service/README.md). The
first-time commands are:

```powershell
cd C:\Dev\P5-TTE-API
python -m venv .\ml-service\.venv
.\ml-service\.venv\Scripts\python.exe -m pip install -r .\ml-service\requirements.txt
Copy-Item .\ml-service\.env.example .\ml-service\.env
```

Edit `.env`, place the selected model file(s) in `ml-service\models`, then run:

```powershell
.\Start-LocalServices.ps1
```

To use the public OSM-derived Nominatim and OSRM services for source and
destination routing:

```powershell
.\Start-LocalServices.ps1 -UseOsrm
```

No API key is required for these public prototype endpoints. They are
community services with usage policies and rate limits, so they are suitable
for occasional development requests, not guaranteed production availability.

## What it does
`POST /api/routes/estimate` is the main combined endpoint. It takes an ordered
list of route segments, departure time, and travel mode. For convenience
during development it can also take a source, destination, and optional via
locations and ask a routing provider to build the segments. The API then:

1. asks the enabled route provider(s) for a route and its ordered segments;
2. chooses the available primary result and records all provider results;
3. turns each segment into one row in the numeric model sequence;
4. calls the configured Python model service, or runs a simple placeholder calculation;
5. returns the estimate together with the route data used.

`POST /api/routes/provider-data` is a provider diagnostic endpoint. It accepts
source, destination, and optional via locations, then returns the selected
provider route, provider timings, geometry, segments, OSM enrichment, and
provider diagnostics without calling the model.

`POST /api/routes/model-only` is a model diagnostic endpoint. It accepts
explicit segments and calls the configured Python model without querying
Nominatim, OSRM, Valhalla, or Overpass. This is useful for controlled model
tests and feature debugging.

The endpoint names are intentionally explicit:

- `estimate` means the complete application flow.
- `provider-data` means routing and OSM/provider diagnostics only.
- `model-only` means model prediction from caller-supplied segments only.

`GET /health` is a basic liveness check.

### Request using an explicit segment collection

When `segments` is present, it is the route input. The API preserves the
segment order, creates one model row per segment, predicts each row, and sums
the resulting `modelSegmentTimeSeconds` values into
`modelRouteTimeSeconds`.

```json
{
  "source": "Stockholm",
  "destination": "Gothenburg",
  "via": [],
  "segments": [
    {
      "lengthMeters": 1000,
      "roadType": "primary",
      "speedLimitKmh": 50,
      "laneCount": 2,
      "isRoundabout": false,
      "hasTrafficSignal": true,
      "isIntersection": true,
      "isBridge": false,
      "isTunnel": false
    },
    {
      "lengthMeters": 500,
      "roadType": "secondary",
      "speedLimitKmh": 30,
      "laneCount": 1,
      "isRoundabout": true,
      "hasTrafficSignal": false,
      "isIntersection": true,
      "isBridge": false,
      "isTunnel": false
    }
  ],
  "departureTime": "2026-10-22T08:30:00+02:00",
  "mode": "car"
}
```

`source` and `destination` are optional for explicit segments. Each segment's
`lengthMeters` is required; the other segment attributes are optional. Map
node identifiers and provider durations are intentionally not accepted in this
manual request because they are provider-owned metadata.

The response includes one prediction per route segment:

```json
{
  "modelRouteTimeSeconds": 135.4,
  "routeDistanceM": 1500,
  "modelName": "route_time_model_v1",
  "route": {
    "segments": [
      { "sequence": 0, "modelSegmentTimeSeconds": 75.1 },
      { "sequence": 1, "modelSegmentTimeSeconds": 60.3 }
    ]
  }
}
```

The displayed values are illustrative. The API always calculates the route
total as the sum of the segment predictions.

Provider duration is deliberately not accepted in a manually supplied request.
When the route is obtained from OSRM or Valhalla, the provider's segment
duration is returned inside provider diagnostics as
`providerSegmentTimeSeconds`. It is not the model prediction.

`matched_length_m` is the model input derived directly from `lengthMeters`.
There is no application-side cap; keep manual segment lengths within the
range supported by the current model until it has been retrained.

### Request using provider routing

If `segments` is omitted or empty, provide both `source` and `destination`.
The configured provider creates the ordered segment collection first:

```json
{
  "source": "Stockholm",
  "destination": "Gothenburg",
  "via": ["Örebro"],
  "segments": [],
  "departureTime": "2026-10-22T08:30:00+02:00",
  "mode": "car"
}
```

The response also includes `distanceMeters`, the selected `route`, provider
results in `route.providerData`, and `route.missingData`.

For a provider-routed request, the response also contains:

```json
{
  "routeProvider": "OSRM",
  "providerRouteTimeSeconds": 1234.5
}
```

These fields are the whole-route provider estimate. Compare them with
`modelRouteTimeSeconds`, which is the sum of the model's individual
segment predictions.

### Provider data only

Use this endpoint to inspect routing and OSM enrichment without involving the
model:

```json
{
  "source": "Stockholm",
  "destination": "Gothenburg",
  "via": [],
  "departureTime": "2026-10-22T08:30:00+02:00",
  "mode": "car"
}
```

The response is a `UnifiedRoute` object. For OSRM routes, enriched segments
can include `osmWayId`, `roadType`, `speedLimitKmh`, `laneCount`, `isBridge`,
and `isTunnel`. `osmWayId` is the best Overpass geometric match for
diagnostics; it is not a guarantee that OSRM exposed or used that exact OSM
way.

### Model estimate from explicit segments

Use this endpoint when provider routing is not part of the test:

```json
{
  "segments": [
    {
      "lengthMeters": 1000,
      "roadType": "residential",
      "speedLimitKmh": 50,
      "laneCount": 2,
      "isRoundabout": false,
      "hasTrafficSignal": true,
      "isIntersection": true,
      "isBridge": false,
      "isTunnel": false
    }
  ],
  "departureTime": "2026-10-22T08:30:00+02:00",
  "mode": "car"
}
```

It returns the same model estimate shape as the combined endpoint, including
`modelRouteTimeSeconds` and per-segment `modelSegmentTimeSeconds`, but has no
provider route time because no provider was queried.

## Current scope and important limitation
The canonical input is `segments`. Each segment is one road part in travel
order. `via` is only for the optional provider-routing convenience path.
The routing provider then creates the actual route geometry and ordered
segments. A user via point is not the same thing as a provider-generated
geometry point or road segment.

The response exposes `route.geometry` and `route.segments`. Segment metadata
is nullable and reports missing fields rather than inventing values.

The API uses OSM-compatible services as route sources:

- **Valhalla** can be the preferred provider when enabled.
- **OSRM** can be enabled as another provider or fallback.
- **Nominatim** is used to turn place names into coordinates for both services.

OSM is map data, not one single routing API. Nominatim geocodes names; OSRM
and Valhalla calculate routes from map data.

## Running locally
From this directory:
```powershell
dotnet run --launch-profile http
```

The default local URL is `http://localhost:5073`. Requests are in
[`TimeEstimationAPI.http`](./TimeEstimationAPI.http).

Run tests from the repository root:
```powershell
dotnet test .\TimeEstimationAPI.Tests\TimeEstimationAPI.Tests.csproj
```

### Running the API and Python model service together

The repository includes a launcher for local use. First create the Python
environment and install its dependencies:

```powershell
cd C:\Dev\P5-TTE-API
python -m venv .\ml-service\.venv
.\ml-service\.venv\Scripts\python.exe -m pip install -r .\ml-service\requirements.txt
```

Place the selected model from `.env` at
`C:\Dev\P5-TTE-API\ml-service\models\XGBRegressor.joblib` (or the filename
matching `MODEL_NAME`), then start both services:

```powershell
.\Start-LocalServices.ps1
```

The launcher starts:

- the Python model service at `http://127.0.0.1:8001`;
- the ASP.NET API at `http://localhost:5073`.

Check both services:

```powershell
Invoke-RestMethod http://127.0.0.1:8001/health
Invoke-RestMethod http://localhost:5073/health
```

To use a model at another path:

```powershell
.\Start-LocalServices.ps1 -ModelPath "D:\models\route-model.joblib"
```

The launcher prints both process IDs. Stop those exact processes when finished:

```powershell
Stop-Process -Id <pythonPid>,<apiPid>
```

#### Two-terminal workflow

After the one-time setup above, use these commands.

Terminal 1 - Python model service:

```powershell
cd C:\Dev\P5-TTE-API\ml-service
Copy-Item .env.example .env
# Edit .env and choose MODEL_NAME after placing the .joblib files in .\models
.\.venv\Scripts\python.exe -m uvicorn app:app --host 127.0.0.1 --port 8001
```

Terminal 2 - ASP.NET API:

```powershell
cd C:\Dev\P5-TTE-API
$env:Model__UsePlaceholder = "false"
$env:Model__UsePythonService = "true"
$env:Model__PythonServiceUrl = "http://127.0.0.1:8001"
dotnet run --project .\TimeEstimationAPI\TimeEstimationAPI.csproj --launch-profile http
```

Keep both terminals open. The Python service must be running before sending
route-estimation requests to the ASP.NET API.

If PowerShell blocks local scripts, allow scripts for the current process only:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Start-LocalServices.ps1
```

## Providers

### Development provider
The default provider is a deterministic in-process development provider. It
does not call the internet. It creates one predictable segment per route leg,
divides a fixed synthetic distance across those segments, and reports explicit
missing fields. This keeps local tests repeatable, but its route is not real
geography and must not be used as a real-world ETA.

### OSRM and Nominatim
Enable OSRM with:
```powershell
$env:Osm__UseOsrm = "true"
dotnet run --no-launch-profile
```

The default endpoints are configured in `appsettings.json`. The API first
geocodes every stop with Nominatim and then sends the coordinates to OSRM.
The public services are free for reasonable prototype use, but can rate-limit
or reject requests. For dependable development, run your own instances or
use a provider approved by the team.

The current OSRM adapter requests full geometry, steps, and annotations. It
geocodes the source, destination, and via locations with Nominatim, maps OSRM
steps to ordered route segments, and reads each step's distance, duration,
intersection, traffic-signal, and available lane information. Its route
summary duration is returned as `providerRouteTimeSeconds` for
comparison with the model's summed segment prediction.

The `departureTime` value is accepted and used for model features. The public
OSRM service does not apply time-dependent traffic to its route duration, so
its provider estimate is a static map-based comparison, not a historical or
live traffic estimate. Road type and speed limit are still missing because
they are not reliably present in the current OSRM response.

OSM is map data rather than a time-estimation API. Nominatim provides
geocoding, while OSRM provides routing and step durations from OSM-derived
data. Querying raw OSM tags alone cannot produce a time-of-day ETA; that
requires a routing engine with a traffic/time model.

OSRM does not reliably return every OSM way tag. Whenever OSRM routing is
enabled, the API automatically sends one or more bounded Overpass queries for
the route area and matches each OSRM step to the nearest OSM `highway` way.
The `highway` filter means “mapped road/path/track way”, not necessarily a
motorway. It provides tags such as `highway=primary`, `highway=residential`,
or `highway=service`; those tags are used to fill missing segment attributes.
They do not prove that the route travelled that way.

Matching uses the complete OSRM step geometry rather than only its midpoint.
This makes parallel urban roads and ramps less likely to receive tags from a
nearby road. A candidate is accepted only when every OSRM geometry point is
within `0.0001` degrees, roughly 11 metres north/south, of that OSM way. This
is a geographic safety threshold, not the width of a road; its metre
equivalent depends slightly on latitude. A smaller threshold can reduce false
matches, while a larger threshold can recover incomplete or imprecise map
geometry. Each query covers at most `0.22` degrees north/south and east/west,
roughly 24 km north/south, so long routes are split into smaller query boxes
instead of being skipped entirely.

This can fill `roadType`, `speedLimitKmh`, `laneCount`, `isBridge`, and
`isTunnel`. The match remains best-effort: a two-dimensional map lookup cannot
reliably distinguish an overpass from the road directly underneath when their
coordinates overlap. Existing positive provider bridge/tunnel information is
preserved, and the original OSRM segment is retained if Overpass is unavailable or no nearby way
is found. A matched segment also exposes `osmWayId`; this is the OpenStreetMap
way ID returned by Overpass for the best geometric match. It is useful for
diagnostics and later inspection, but it is not proof that OSRM used that way.
The public OSRM response does not reliably provide OSM way IDs for each step,
and obtaining the ID here does not require payment. This approach is suitable
for urban routes, but it is not a full map-matching engine and should not be
treated as authoritative road identity.

Nominatim, OSRM, and Overpass are free public community services. They require
no API key here, but they have rate limits and no uptime guarantee. For
stability without paying, keep requests occasional, cache route results when
possible, use a clear User-Agent, and avoid parallel bulk requests. A fully
reliable high-volume setup requires self-hosting the data/services.

### Valhalla
Enable a local Valhalla service with:
```powershell
$env:Valhalla__Enabled = "true"
$env:Osm__UseOsrm = "true"
dotnet run --no-launch-profile
```

The default Valhalla endpoint is `http://localhost:8002/route`. The current
adapter geocodes the stops with Nominatim, sends them to Valhalla with
`costing: "auto"`, and reads `trip.summary.length` and
`trip.summary.time`, and maneuver data when present. Maneuvers become ordered
route segments. Road class, speed limit, lane count, and traffic-signal
availability are still reported as missing unless the provider response
supplies them.

When Valhalla is enabled and returns usable data, it is selected before other
providers. If it fails, the collector records the failure and uses the first
other available provider. If every provider fails, the endpoint returns an
error.

Valhalla does not automatically appear just because the API is configured. A
Valhalla server must already be running with map tiles. The API currently does
not download PBF files or build Valhalla tiles.

## Model
Development uses the placeholder by default:
```json
"Model": {
  "UsePlaceholder": true,
  "UsePythonService": false,
  "PythonServiceUrl": "http://localhost:8001",
  "PythonServiceTimeoutSeconds": 30,
  "PlaceholderAverageSpeedKmh": 40
}
```

The placeholder is a transparent baseline: distance divided by an assumed
speed, plus a small signal-delay assumption when signal data exists. It is not
traffic data and it is not a trained model.

### MLM model integration

The ML team supplies the trained model as a Python `.joblib` artifact. The
ASP.NET API does not load `.joblib` directly. Instead, the companion Python
service in [`ml-service`](../ml-service) loads the artifact and exposes
prediction over HTTP.

```text
C:\Dev\P5-TTE-API\ml-service\models\XGBRegressor.joblib
```

Start the Python service on port `8001`, then configure the ASP.NET API:

```json
"Model": {
  "UsePlaceholder": false,
  "UsePythonService": true,
  "PythonServiceUrl": "http://localhost:8001",
  "PythonServiceTimeoutSeconds": 30
}
```

The model's `FEATURES` entries are the source of truth for
[`FeatureTransformer.cs`](./Services/FeatureTransformer.cs). Each feature must
be mapped by its exact name and training definition, including order, units,
normalization, categorical encoding, missing-value handling, and whether it is
a segment-level or route-level value. The current supplied joblib artifacts expose this segment-level feature order:

```text
matched_length_m, hour, minute, day_of_week, month, speed_limit_kmh, density,
lane_count, bridge, tunnel, roundabout, internal_intersection
```

The current transformer maps available route data to these names. `density`,
`bridge`, and `tunnel` are currently sent as zero because the active
route providers do not expose them; confirm that convention with the ML team
before using production estimates.

The Python service is configured for segment-level input. Each ordered route
segment becomes one row containing the model's 12 features. The Python service
predicts every row and sums those predictions into the route estimate.
`route.segments[*].modelSegmentTimeSeconds` contains the individual segment
predictions.

The ASP.NET adapter sends named `routeFeatures`, `segmentFeatures`, and
`timeFeatures` objects to the Python service. The exact feature names, order, and types still need confirmation from the ML
team. The API requires segment input mode.

Direct Python service request:

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
    },
    {
      "matched_length_m": 500,
      "hour": 8,
      "minute": 30,
      "day_of_week": 4,
      "month": 10,
      "speed_limit_kmh": 30,
      "density": 0,
      "lane_count": 1,
      "bridge": 0,
      "tunnel": 0,
      "roundabout": 1,
      "internal_intersection": 1
    }
  ],
  "timeFeatures": {}
}
```

Expected response shape:

```json
{
  "estimated_travel_time_seconds": 135.4,
  "segment_predictions": [75.1, 60.3]
}
```

## Main files
- `Program.cs` — dependency injection and the two HTTP endpoints.
- `Models/RouteRequest.cs` — request JSON shape and basic limits.
- `Models/UnifiedRoute.cs` — canonical route data, geometry, segments, and provider provenance.
- `Services/RouteDataCollector.cs` — calls providers and chooses the route.
- `Services/RouteProvider.cs` — provider contract, OSRM adapter, and development provider.
- `Services/ValhallaService.cs` — Valhalla adapter.
- `Services/FeatureTransformer.cs` — maps route data to model numbers.
- `Services/ModelRunner.cs` — placeholder prediction and model-runner contract.
- `Services/PythonModelRunner.cs` — HTTP client for the Python `.joblib` service.
- `../ml-service/app.py` — Python model loading and prediction service.
- `Services/ModelService.cs` — coordinates the estimate pipeline.
- `Middleware/ApiExceptionHandler.cs` — consistent error responses.

The project intentionally does not include Google benchmarking or a scheduled
map-download job. Those are useful later, but they are not required to return
an estimate and would make the core API harder to understand.

## Open TODOs
The repository-root [`TODO.md`](../TODO.md) contains the important unresolved
design decisions. In particular, do not assume that the current
`RouteSegment` objects are the final model segments: first confirm whether the
ML model uses Valhalla maneuvers, Valhalla edges, OSM ways, or another unit.
That file also tracks local Valhalla setup, model feature normalization, and
UTC versus local-time semantics.
