using System.ComponentModel.DataAnnotations;

namespace TimeEstimationAPI.Models;

/// <summary>Describes either an existing route or a route to obtain from a provider.</summary>
public sealed record RouteRequest
{
    [MaxLength(200)]
    public string? Source { get; init; }

    [MaxLength(200)]
    public string? Destination { get; init; }

    /// <summary>User-selected places the provider must route through.</summary>
    [MaxLength(50)]
    public List<string> Via { get; init; } = [];

    /// <summary>Ordered road segments supplied by the caller.</summary>
    public List<RouteSegmentInput> Segments { get; init; } = [];

    public DateTimeOffset DepartureTime { get; init; } = DateTimeOffset.UtcNow;

    public string Mode { get; init; } = "car";
}

public sealed record RouteSegmentInput
{
    public double LengthMeters { get; init; }
    public double? DurationSeconds { get; init; }
    public string? RoadType { get; init; }
    public double? SpeedLimitKmh { get; init; }
    public int? LaneCount { get; init; }
    public bool? IsRoundabout { get; init; }
    public bool? HasTrafficSignal { get; init; }
    public bool? IsIntersection { get; init; }
    public List<long> NodeIds { get; init; } = [];
}
