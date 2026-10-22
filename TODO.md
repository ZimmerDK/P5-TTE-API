# Project TODOs

These are deliberately kept at the repository root so they are easy to find
before changing routing, feature engineering, or model integration.

## Decide what a model segment is

- [ ] Confirm the model's exact segment unit with the ML team.
- [ ] Decide whether one segment means a Valhalla maneuver, Valhalla edge,
      OSM way, road section between intersections, or a project-specific unit.
- [ ] Make OSRM and Valhalla produce that same unit, or document a deliberate
      conversion between their units.
- [ ] Only then treat `RouteSegment` as the final domain name. It is currently
      intentionally neutral because this decision is still open.

## Make Valhalla work locally

- [ ] Set up and verify a local Valhalla server with a small OSM `.osm.pbf`
      extract and generated Valhalla tiles.
- [ ] Prefer a separate setup script and/or container for downloading map data,
      building tiles, and starting Valhalla. It can live in this GitHub project
      folder, but it should not run as part of the API request path.
- [ ] Capture a real Valhalla response and update the adapter/tests from the
      actual response shape.

## Improve OSM enrichment

- [x] Query OSM way geometry through Overpass and split large route areas into
      smaller bounded requests.
- [x] Match the full OSRM step geometry rather than only its midpoint.
- [x] Preserve provider bridge/tunnel signals when two roads overlap in 2D.
- [x] Expose the matched Overpass `osmWayId` for segment diagnostics.
- [ ] Add mocked OSRM/Overpass tests for parallel urban roads, ramps, and
      bridge/underpass cases.
- [ ] Use a provider-supported way identifier or a full map-matching engine if
      authoritative road identity is required.

## Align model features

- [x] Implement segment-level prediction: one model row and one prediction per
      ordered route segment, with route time equal to their sum.
- [ ] Verify the implementation with the ML team's expected multi-segment
      request and confirm the trained model's output semantics.
- [ ] Confirm that `matched_length_m` is the map-matched length of each
      individual segment, not the full route length.
- [ ] Confirm the current zero defaults for `density`, `bridge`, and `tunnel`
      against the training pipeline.
- [ ] Replace the provisional feature order, units, clamps, normalization
      divisors, road-type codes, and placeholder environment value in
      `FeatureTransformer` with the ML team's training contract.
- [ ] Confirm whether missing values use zero, imputation, masks, or one flag
      per feature.
- [x] Keep provider duration out of the model input; expose it only as a
      comparison/diagnostic value.

## Define time semantics

- [ ] Decide whether departure-time features use UTC, the route's local
      timezone, or an explicitly supplied timezone.
- [ ] Make the API and training pipeline use the same choice, including daylight
      saving-time behavior.
