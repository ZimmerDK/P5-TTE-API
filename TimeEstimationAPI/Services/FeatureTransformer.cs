using TimeEstimationAPI.Models;

namespace TimeEstimationAPI.Services;

/// <summary>Maps each ordered route segment to one model sequence row.</summary>
public sealed class FeatureTransformer
{
    public RouteFeatureVector Transform(UnifiedRoute route)
    {
        var localTime = route.DepartureTime.ToLocalTime();
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

        var sequence = segments
            .OrderBy(segment => segment.Sequence)
            .Select(segment => new[]
            {
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

        return new RouteFeatureVector
        {
            Sequence = sequence,
            TimeFeatures = timeFeatures,
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
