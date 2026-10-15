# Time Estimation API

Small ASP.NET Core API that estimates a route's travel time.

## What it does

`POST /api/routes/estimate` takes a source, destination, optional waypoints,
departure time, and travel mode. The API then:

1. asks the enabled route provider(s) for a route and its ordered segments;
2. chooses the available primary result and records all provider results;
3. turns each segment into one row in the numeric model sequence;
4. runs the configured ONNX model, or a simple placeholder calculation;
5. returns the estimate together with the route data used.

`GET /health` is a basic liveness check.

Example request:

```json
{
  "source": "Stockholm",
  "destination": "Gothenburg",
  "waypoints": [],
  "departureTime": "2026-10-08T12:00:00Z",
  "mode": "car"
}
```

The response includes `estimatedTravelTimeSeconds`, `distanceMeters`, the
selected `route`, provider results in `route.providerData`, and
`route.missingData`.

## Current scope and important limitation

The endpoint accepts named locations and optional user-selected via points.
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

## Providers

### Development stub

The default provider is a deterministic in-process stub. It does not call the
internet. It returns a predictable distance based on the waypoint count,
rough road metadata, and explicit missing fields. This keeps local tests
repeatable, but its route is not real geography and must not be used as a
real-world ETA.

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
maps steps to route segments and can identify some intersections and traffic
signals. Road type, speed limit, and lane count are still missing because
they are not reliably present in the current OSRM response.

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
  "PlaceholderAverageSpeedKmh": 40
}
```

The placeholder is a transparent baseline: distance divided by an assumed
speed, plus a small signal-delay assumption when signal data exists. It is not
traffic data and it is not a trained model.

To try an ONNX model:

```powershell
$env:Model__UsePlaceholder = "false"
dotnet run --no-launch-profile
```

`ModelPath` defaults to `best_model.onnx`. The file is loaded only when
`UsePlaceholder` is `false`. Matching tensor shapes are not enough to prove
that an old model is scientifically compatible: feature order, scaling,
training data, target units, and output meaning must also match.

The current feature mapping is in
[`FeatureTransformer.cs`](./Services/FeatureTransformer.cs). Every segment
becomes one row with length, speed, road-type code, lanes, roundabout,
traffic-signal, intersection, and missing-data features. This is a
provisional contract and must be aligned with the team's training pipeline
before treating the output as a production estimate.

The ONNX adapter writes rows as `sequence[0, segmentIndex, featureIndex]`.
The exact sequence length and feature ordering still need confirmation from
the ML team.

## Main files

- `Program.cs` — dependency injection and the two HTTP endpoints.
- `Models/RouteRequest.cs` — request JSON shape and basic limits.
- `Models/UnifiedRoute.cs` — canonical route data, geometry, segments, and provider provenance.
- `Services/RouteDataCollector.cs` — calls providers and chooses the route.
- `Services/RouteProvider.cs` — provider contract, OSRM adapter, and stub.
- `Services/ValhallaService.cs` — Valhalla adapter.
- `Services/FeatureTransformer.cs` — maps route data to model numbers.
- `Services/ModelRunner.cs` — ONNX loading or placeholder prediction.
- `Services/ModelService.cs` — coordinates the estimate pipeline.
- `Middleware/ApiExceptionHandler.cs` — consistent error responses.

The project intentionally does not include Google benchmarking or a scheduled
map-download job. Those are useful later, but they are not required to return
an estimate and would make the core API harder to understand.
