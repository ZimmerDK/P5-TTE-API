DISCLAIMER: This document is currently primarily AI-generated. 

# Time Estimation API
Small ASP.NET Core API that estimates a route's travel time.

## What it does
`POST /api/routes/estimate` takes an ordered list of route segments, departure
time, and travel mode. For convenience during development it can also take a
source, destination, and optional via locations and ask a routing provider to
build the segments. The API then:

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
  "via": [],
  "segments": [
    {
      "lengthMeters": 1000,
      "speedLimitKmh": 50,
      "laneCount": 2,
      "isRoundabout": false,
      "hasTrafficSignal": true,
      "isIntersection": true,
      "nodeIds": []
    }
  ],
  "departureTime": "2026-10-08T12:00:00Z",
  "mode": "car"
}
```

The response includes `estimatedTravelTimeSeconds`, `distanceMeters`, the
selected `route`, provider results in `route.providerData`, and
`route.missingData`.

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

An ONNX model can be supplied later by setting `ModelPath` and
`UsePlaceholder` to `false`. The old `best_model.onnx` from another project is
not included or used. Matching tensor shapes are not enough to prove that a
model is scientifically compatible: feature order, scaling, training data,
target units, and output meaning must also match.

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
- `Services/RouteProvider.cs` — provider contract, OSRM adapter, and development provider.
- `Services/ValhallaService.cs` — Valhalla adapter.
- `Services/FeatureTransformer.cs` — maps route data to model numbers.
- `Services/ModelRunner.cs` — ONNX loading or placeholder prediction.
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
