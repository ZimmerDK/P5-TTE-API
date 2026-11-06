using TimeEstimationAPI.Models;

namespace TimeEstimationAPI.Services;

/// <summary>Predictable offline provider used for local development and tests.</summary>
public sealed class DevelopmentRouteProvider : IRouteProvider
{
    public string Name => "development-stub";

    public Task<ProviderRouteResult> GetRouteMetadataAsync(
        string source,
        string destination,
        List<string>? waypoints,
        CancellationToken cancellationToken = default)
    {
        var segmentCount = Math.Max((waypoints?.Count ?? 0) + 1, 1);
        var segments = Enumerable.Range(0, segmentCount)
            .Select(index => new RouteSegment
            {
                Sequence = index,
                LengthMeters = 6500d / segmentCount,
                ProviderSegmentTimeSeconds = null,
                RoadType = "primary",
                SpeedLimitKmh = 50,
                MissingData = ["providerSegmentTimeSeconds"]
            })
            .ToArray();

        return Task.FromResult(new ProviderRouteResult
        {
            Provider = Name,
            DistanceMeters = 6500,
            RoadType = "primary",
            SpeedLimitKmh = 50,
            MissingFields = ["providerRouteTimeSeconds", "trafficSignalCount"],
            Segments = segments
        });
    }
}
