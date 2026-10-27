namespace TimeEstimationAPI.Models;

using System.Text.Json.Serialization;

public sealed record RoutePoint(double Longitude, double Latitude);

public sealed record RouteSegment
{
    public int Sequence { get; init; }
    public IReadOnlyList<RoutePoint> Geometry { get; init; } = [];
    public IReadOnlyList<long> NodeIds { get; init; } = [];
    public double? LengthMeters { get; init; }
    public double? ProviderSegmentTimeSeconds { get; init; }
    public double? ModelSegmentTimeSeconds { get; init; }
    public long? OsmWayId { get; init; }
    public string? RoadType { get; init; }
    public double? SpeedLimitKmh { get; init; }
    public int? LaneCount { get; init; }
    public bool? IsRoundabout { get; init; }
    public bool? HasTrafficSignal { get; init; }
    public bool? IsIntersection { get; init; }
    public bool? IsBridge { get; init; }
    public bool? IsTunnel { get; init; }
    public IReadOnlyCollection<string> MissingData { get; init; } = [];
}

/// <summary>One provider's observations about a route.</summary>
public sealed record RouteProviderData
{
    public required string Provider { get; init; }
    public double? DistanceMeters { get; init; }
    public double? ProviderRouteTimeSeconds { get; init; }
    public string? RoadType { get; init; }
    public double? SpeedLimitKmh { get; init; }
    public int? TrafficSignalCount { get; init; }
    public bool IsAvailable { get; init; }
    public IReadOnlyCollection<string> MissingFields { get; init; } = [];

    [JsonIgnore]
    public IReadOnlyList<RouteSegment> Segments { get; init; } = [];
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
    public IReadOnlyList<string> Via { get; init; } = [];

    public IReadOnlyList<RouteSegment> Segments { get; init; } = [];
    public DateTimeOffset DepartureTime { get; init; }
    public required string Mode { get; init; }
    public double? DistanceMeters { get; init; }
    public string? RoadType { get; init; }
    public double? SpeedLimitKmh { get; init; }
    public int? TrafficSignalCount { get; init; }
    public double? ProviderRouteTimeSeconds { get; init; }
    public string? SelectedProvider { get; init; }
    public IReadOnlyCollection<string> MissingData { get; init; } = [];
    public IReadOnlyList<RouteProviderData> ProviderData { get; init; } = [];
}
