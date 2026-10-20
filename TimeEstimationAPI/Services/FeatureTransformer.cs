using TimeEstimationAPI.Models;

namespace TimeEstimationAPI.Services;

/// <summary>Maps each ordered route segment to one model sequence row.</summary>
public sealed class FeatureTransformer
{
    public RouteFeatureVector Transform(UnifiedRoute route)
    {
        // TODO: Confirm whether MLM training uses UTC, the route's local timezone, or another explicit timezone.
        // To avoid silently changing behavior, this currently uses the machine's local timezone.
        var localTime = route.DepartureTime.ToLocalTime();
        /*var utcTime = route.DepartureTime.UtcDateTime;*/
        var hour = localTime.Hour + localTime.Minute / 60.0;
        var minute = localTime.Minute + localTime.Second / 60.0;

        var timeFeatures = new[]
        {
            Math.Sin(2 * Math.PI * hour / 24.0),
            Math.Cos(2 * Math.PI * hour / 24.0),
            Math.Sin(2 * Math.PI * minute / 60.0),
            Math.Cos(2 * Math.PI * minute / 60.0)
        };

        var segments = route.Segments.Count > 0
            ? route.Segments
            : [new RouteSegment
            {
                Sequence = 0,
                LengthMeters = route.DistanceMeters,
                RoadType = route.RoadType,
                SpeedLimitKmh = route.SpeedLimitKmh,
                IsIntersection = route.TrafficSignalCount > 0,
                MissingData = ["segments"]
            }];

        var orderedSegments = segments.OrderBy(segment => segment.Sequence).ToArray();
        var sequence = orderedSegments
            .OrderBy(segment => segment.Sequence)
            .Select(segment => new[]
            {
                // TODO(ML): Replace every provisional unit, clamp, divisor, and category code below with the training pipeline's exact schtuff.
                Math.Max(segment.LengthMeters ?? 0, 0) / 1000.0,
                Math.Clamp(segment.SpeedLimitKmh ?? 0, 0, 130) / 130.0,
                RoadTypeCode(segment.RoadType),
                Math.Clamp(segment.LaneCount ?? 0, 0, 8) / 8.0,
                segment.IsRoundabout == true ? 1.0 : 0.0,
                segment.HasTrafficSignal == true ? 1.0 : 0.0,
                segment.IsIntersection == true ? 1.0 : 0.0,
                1.0, // urban/weather data is not supplied by the providers yet
                segment.MissingData.Count > 0 || route.MissingData.Count > 0 ? 1.0 : 0.0,
                Math.Max(segment.DurationSeconds ?? 0, 0) / 3600.0
            })
            .ToArray();
        var segmentFeatures = orderedSegments
            .Select((segment, index) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["lengthKm"] = sequence[index][0],
                ["speedLimitNormalized"] = sequence[index][1],
                ["roadTypeCode"] = sequence[index][2],
                ["laneCountNormalized"] = sequence[index][3],
                ["isRoundabout"] = sequence[index][4],
                ["hasTrafficSignal"] = sequence[index][5],
                ["isIntersection"] = sequence[index][6],
                ["environmentPlaceholder"] = sequence[index][7],
                ["missingData"] = sequence[index][8],
                ["providerDurationHours"] = sequence[index][9]
            })
            .ToArray();
        var localDeparture = route.DepartureTime.ToLocalTime();
        var routeSpeedLimit = orderedSegments
            .Select(segment => segment.SpeedLimitKmh)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(0)
            .Average();
        var routeFeatures = new Dictionary<string, object?>
        {
            ["matched_length_m"] = Math.Max(route.DistanceMeters ?? orderedSegments.Sum(segment => segment.LengthMeters ?? 0), 0),
            ["hour"] = localDeparture.Hour,
            ["minute"] = localDeparture.Minute,
            ["day_of_week"] = (int)localDeparture.DayOfWeek,
            ["month"] = localDeparture.Month,
            ["speed_limit_kmh"] = routeSpeedLimit,
            ["density"] = 0,
            ["lane_count"] = orderedSegments
                .Select(segment => segment.LaneCount)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .DefaultIfEmpty(0)
                .Average(),
            ["bridge"] = 0,
            ["tunnel"] = 0,
            ["roundabout"] = orderedSegments.Any(segment => segment.IsRoundabout == true) ? 1 : 0,
            ["internal_intersection"] = orderedSegments.Any(segment => segment.IsIntersection == true) ? 1 : 0
        };

        return new RouteFeatureVector
        {
            Sequence = sequence,
            TimeFeatures = timeFeatures,
            SegmentFeatures = segmentFeatures,
            RouteFeatures = routeFeatures,
            ModelName = "route_time_model_v1"
        };
    }

    private static double RoadTypeCode(string? roadType) =>
        roadType?.ToLowerInvariant() switch
        {
            "motorway" => 3,
            "trunk" => 2.5,
            "primary" => 2,
            "secondary" => 1.5,
            _ => 1
        };
}
