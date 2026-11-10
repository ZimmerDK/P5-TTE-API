using TimeEstimationAPI.Models;

namespace TimeEstimationAPI.Services;

public interface IRouteProvider
{
    string Name { get; }

    Task<ProviderRouteResult> GetRouteMetadataAsync(
        string source,
        string destination,
        List<string>? waypoints,
        CancellationToken cancellationToken = default);
}

/// <summary>Common route data returned by a routing provider.</summary>
public sealed class ProviderRouteResult
{
    public string Provider { get; set; } = "OSRM";
    public double? DistanceMeters { get; set; }
    public double? ProviderRouteTimeSeconds { get; set; }
    public string? RoadType { get; set; }
    public double? SpeedLimitKmh { get; set; }
    public int? TrafficSignalCount { get; set; }
    public bool IsAvailable { get; set; } = true;
    public IReadOnlyCollection<string> MissingFields { get; set; } = [];
    public IReadOnlyList<RouteSegment> Segments { get; set; } = [];
}

public sealed class RouteProviderUnavailableException(string message)
    : Exception(message);

public sealed class InvalidViaLocationException(string location)
    : ArgumentException($"The via location '{location}' could not be geocoded. Provide a valid address or coordinate.");
