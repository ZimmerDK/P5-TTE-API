namespace TimeEstimationAPI.Models;

/// <summary>One provider's observations about a route.</summary>
public sealed record RouteProviderData
{
    public required string Provider { get; init; }
    public double? DistanceMeters { get; init; }
    public double? DurationSeconds { get; init; }
    public string? RoadType { get; init; }
    public double? SpeedLimitKmh { get; init; }
    public int? TrafficSignalCount { get; init; }
    public bool IsAvailable { get; init; }
    public IReadOnlyCollection<string> MissingFields { get; init; } = [];
    public IReadOnlyDictionary<string, object> Raw { get; init; } =
        new Dictionary<string, object>();
}

/// <summary>
/// Canonical route assembled from the request and all available routing
/// provider data. This is the object used for feature engineering and
/// returned as route diagnostics.
/// </summary>
public sealed record UnifiedRoute
{
    public required string Source { get; init; }
    public required string Destination { get; init; }
    public IReadOnlyList<string> Waypoints { get; init; } = [];
    public DateTimeOffset DepartureTime { get; init; }
    public required string Mode { get; init; }
    public double? DistanceMeters { get; init; }
    public string? RoadType { get; init; }
    public double? SpeedLimitKmh { get; init; }
    public int? TrafficSignalCount { get; init; }
    public double? RouteDurationSeconds { get; init; }
    public IReadOnlyCollection<string> MissingData { get; init; } = [];
    public IReadOnlyList<RouteProviderData> ProviderData { get; init; } = [];
}
