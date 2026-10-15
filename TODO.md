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

## Align model features

- [ ] Replace the provisional feature order, units, clamps, normalization
      divisors, road-type codes, and placeholder environment value in
      `FeatureTransformer` with the ML team's training contract.
- [ ] Confirm whether missing values use zero, imputation, masks, or one flag
      per feature.
- [ ] Confirm whether provider duration belongs in the model input at all.

## Define time semantics

- [ ] Decide whether departure-time features use UTC, the route's local
      timezone, or an explicitly supplied timezone.
- [ ] Make the API and training pipeline use the same choice, including daylight
      saving-time behavior.
