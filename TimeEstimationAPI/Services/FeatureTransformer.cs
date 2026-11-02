using TimeEstimationAPI.Models;

namespace TimeEstimationAPI.Services;

/// <summary>Maps each ordered route segment to one model sequence row.</summary>
public sealed class FeatureTransformer
{
    /// <param name="route">The route to build model rows for.</param>
    /// <param name="segmentStartOffsetsSeconds">
    /// Optional seconds after departure at which each ordered segment is entered. When given, each
    /// segment's time features use that moment instead of the departure time.
    /// </param>
    public RouteFeatureVector Transform(UnifiedRoute route, IReadOnlyList<double>? segmentStartOffsetsSeconds = null)
    {
        var localDeparture = route.DepartureTime.ToLocalTime();
        var hour = localDeparture.Hour + localDeparture.Minute / 60.0;
        var minute = localDeparture.Minute + localDeparture.Second / 60.0;

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
            .Select(segment => new[]
            {
                Math.Max(segment.LengthMeters ?? 0, 0) / 1000.0,
                Math.Clamp(segment.SpeedLimitKmh ?? 0, 0, 130) / 130.0,
                RoadTypeCode(segment.RoadType),
                Math.Clamp(segment.LaneCount ?? 0, 0, 8) / 8.0,
                segment.IsRoundabout == true ? 1.0 : 0.0,
                segment.HasTrafficSignal == true ? 1.0 : 0.0,
                segment.IsIntersection == true ? 1.0 : 0.0,
                1.0,
                segment.MissingData.Count > 0 || route.MissingData.Count > 0 ? 1.0 : 0.0
            })
            .ToArray();

        var segmentFeatures = orderedSegments
            .Select((segment, index) =>
            {
                var roadClassId = RoadClassId(segment.RoadType);
                var prevRoadClassId = index > 0 ? RoadClassId(orderedSegments[index - 1].RoadType) : roadClassId;
                var nextRoadClassId = index < orderedSegments.Length - 1 ? RoadClassId(orderedSegments[index + 1].RoadType) : roadClassId;
                var popDensity = Math.Max(segment.PopulationDensity ?? 0, 0);
                var builtUpFrac = Math.Max(segment.BuiltUpFraction ?? 0, 0);
                var roadDensity = Math.Max(segment.RoadDensity ?? segment.Density ?? 0, 0);
                var builtVolDensity = Math.Max(segment.BuiltVolumeDensity ?? 0, 0);
                var isLink = segment.RoadType?.Contains("link", StringComparison.OrdinalIgnoreCase) == true ? 1 : 0;
                var isOneway = segment.IsOneway == true;
                var enterTime = segmentStartOffsetsSeconds is null
                    ? localDeparture
                    : localDeparture.AddSeconds(segmentStartOffsetsSeconds[index]);

                // Value for this radius from the context service, or the fallback when it is not available
                double Context(string featureName, double fallback) =>
                    segment.ContextFeatures.TryGetValue(featureName, out var value) ? Math.Max(value, 0) : fallback;

                return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["road_class_id"] = roadClassId,
                    ["lanes"] = Math.Max(segment.LaneCount ?? 1, 1),
                    ["is_link"] = isLink,
                    ["speed_limit_kmh"] = Math.Max(segment.SpeedLimitKmh ?? 50.0, 0),
                    ["bridge"] = segment.IsBridge == true ? 1 : 0,
                    ["tunnel"] = segment.IsTunnel == true ? 1 : 0,
                    ["roundabout"] = segment.IsRoundabout == true ? 1 : 0,
                    ["prev_road_class_id"] = prevRoadClassId,
                    ["next_road_class_id"] = nextRoadClassId,
                    ["length_m"] = Math.Max(segment.LengthMeters ?? 0, 0),
                    ["pop_density_100m"] = Context("pop_density_100m", popDensity),
                    ["built_up_fraction_100m"] = Context("built_up_fraction_100m", builtUpFrac),
                    ["road_density_100m"] = Context("road_density_100m", roadDensity),
                    ["intersection_density_100m"] = Context("intersection_density_100m", 0.0),
                    ["pop_density_500m"] = Context("pop_density_500m", popDensity),
                    ["built_up_fraction_500m"] = Context("built_up_fraction_500m", builtUpFrac),
                    ["built_volume_density_500m"] = Context("built_volume_density_500m", builtVolDensity),
                    ["road_density_500m"] = Context("road_density_500m", roadDensity),
                    ["intersection_density_500m"] = Context("intersection_density_500m", 0.0),
                    ["four_plus_intersection_density_500m"] = Context("four_plus_intersection_density_500m", 0.0),
                    ["pop_density_2000m"] = Context("pop_density_2000m", popDensity),
                    ["built_up_fraction_2000m"] = Context("built_up_fraction_2000m", builtUpFrac),
                    ["road_density_2000m"] = Context("road_density_2000m", roadDensity),
                    ["intersection_density_2000m"] = Context("intersection_density_2000m", 0.0),
                    ["settlement_code"] = Context("settlement_code", 30),
                    ["month_enter_time"] = enterTime.Month,
                    ["dayOfMonth_enter_time"] = enterTime.Day,
                    ["hour_enter_time"] = enterTime.Hour,
                    ["minute_enter_time"] = enterTime.Minute,
                    // Monday = 0 ... Sunday = 6 like pandas' dayofweek in training (C# DayOfWeek starts at Sunday = 0)
                    ["dayOfWeek_enter_time"] = ((int)enterTime.DayOfWeek + 6) % 7,
                    ["weekend_enter_time"] = enterTime.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1 : 0,
                    ["False_oneway_bool"] = isOneway ? 0 : 1,
                    ["True_oneway_bool"] = isOneway ? 1 : 0,

                    // Backward-compatibility fields
                    ["matched_length_m"] = Math.Max(segment.LengthMeters ?? 0, 0),
                    ["hour"] = enterTime.Hour,
                    ["minute"] = enterTime.Minute,
                    ["day_of_week"] = (int)enterTime.DayOfWeek,
                    ["month"] = enterTime.Month,
                    ["density"] = roadDensity,
                    ["lane_count"] = Math.Max(segment.LaneCount ?? 1, 1),
                    ["internal_intersection"] = segment.IsIntersection == true ? 1 : 0
                };
            })
            .ToArray();

        return new RouteFeatureVector
        {
            Sequence = sequence,
            TimeFeatures = timeFeatures,
            SegmentFeatures = segmentFeatures,
            RouteFeatures = new Dictionary<string, object?>
            {
                ["matched_length_m"] = Math.Max(route.DistanceMeters ?? orderedSegments.Sum(segment => segment.LengthMeters ?? 0), 0),
                ["hour"] = localDeparture.Hour,
                ["minute"] = localDeparture.Minute,
                ["day_of_week"] = (int)localDeparture.DayOfWeek,
                ["month"] = localDeparture.Month
            },
            ModelName = "route_time_model_v1"
        };
    }

    /// <summary>
    /// Road class numbering used by the training data (road_class_id). A *_link road gets the class of
    /// its main road (is_link marks it separately), living_street counts as residential, and every
    /// other type falls in the last class, like in training.
    /// </summary>
    private static int RoadClassId(string? roadType) =>
        roadType?.ToLowerInvariant().Replace("_link", "") switch
        {
            "motorway" => 0,
            "trunk" => 1,
            "primary" => 2,
            "secondary" => 3,
            "tertiary" => 4,
            "unclassified" => 5,
            "residential" or "living_street" => 6,
            _ => 7
        };

    /// <summary>Scaled road type used by the placeholder model's sequence input.</summary>
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
