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
    /// <summary>Physical length, in meters, of this caller-supplied route segment.</summary>
    public double LengthMeters { get; init; }
    /// <summary>Road classification reported by the caller when known.</summary>
    public string? RoadType { get; init; }
    /// <summary>Posted or inferred speed limit in kilometres per hour.</summary>
    public double? SpeedLimitKmh { get; init; }
    /// <summary>Number of lanes when known.</summary>
    public int? LaneCount { get; init; }
    /// <summary>Whether this segment is part of a roundabout.</summary>
    public bool? IsRoundabout { get; init; }
    /// <summary>Whether a traffic signal is associated with this segment.</summary>
    public bool? HasTrafficSignal { get; init; }
    /// <summary>Whether this segment represents an intersection.</summary>
    public bool? IsIntersection { get; init; }
}
