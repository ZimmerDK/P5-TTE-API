namespace TimeEstimationAPI.Services;

using TimeEstimationAPI.Models;
using System.Text.Json;

/// <summary>Gets provider data and folds it into one route object for the model.</summary>
public sealed class RouteDataCollector(
    IEnumerable<IRouteProvider> providers,
    ILogger<RouteDataCollector> logger,
    OsmTagEnricher osmTagEnricher)
{
    /// <summary>Collects route data and records which fields were not available.</summary>
    public async Task<UnifiedRoute> CollectAsync(RouteRequest request, CancellationToken cancellationToken)
    {
        var source = request.Source
            ?? throw new ArgumentException("Source is required when routing from locations.");
        var destination = request.Destination
            ?? throw new ArgumentException("Destination is required when routing from locations.");
        var providerData = new List<RouteProviderData>();
        foreach (var provider in providers)
        {
            try
            {
                var data = await provider.GetRouteMetadataAsync(
                    source, destination, request.Via, cancellationToken);
                // OSRM supplies the route geometry; Overpass supplies the OSM tags.
                if (provider is OsrmService)
                    data.Segments = await osmTagEnricher.EnrichAsync(data.Segments, cancellationToken);

                // Split long segments into smaller sub-segments (< 200 meters) for high-accuracy ML estimation.
                data.Segments = SplitSegments(data.Segments, 200.0);
                providerData.Add(ToProviderData(data));
            }
            catch (HttpRequestException exception)
            {
                RecordProviderFailure(provider, providerData, "provider-unavailable", exception);
            }
            catch (InvalidOperationException exception)
            {
                RecordProviderFailure(provider, providerData, "provider-invalid-response", exception);
            }
            catch (JsonException exception)
            {
                RecordProviderFailure(provider, providerData, "provider-invalid-json", exception);
            }
        }

        var primary = providerData.FirstOrDefault(data => data.Provider == "Valhalla" && data.IsAvailable)
            ?? providerData.FirstOrDefault(data => data.IsAvailable)
            ?? throw new InvalidOperationException("No route provider returned usable data.");

        var missingData = new HashSet<string>(primary.MissingFields, StringComparer.OrdinalIgnoreCase);
        if (!primary.DistanceMeters.HasValue) missingData.Add("distanceMeters");
        if (!primary.ProviderRouteTimeSeconds.HasValue)
            missingData.Add("providerRouteTimeSeconds");
        if (!primary.SpeedLimitKmh.HasValue) missingData.Add("speedLimitKmh");
        if (string.IsNullOrWhiteSpace(primary.RoadType)) missingData.Add("roadType");
        if (primary.Segments.Count == 0) missingData.Add("segments");

        return new UnifiedRoute
        {
            Source = source,
            Destination = destination,
            Via = request.Via,
            DepartureTime = request.DepartureTime,
            Mode = request.Mode,
            DistanceMeters = primary.DistanceMeters,
            RoadType = primary.RoadType,
            SpeedLimitKmh = primary.SpeedLimitKmh,
            TrafficSignalCount = primary.TrafficSignalCount,
            ProviderRouteTimeSeconds = primary.ProviderRouteTimeSeconds,
            SelectedProvider = primary.Provider,
            Geometry = primary.Geometry,
            Segments = primary.Segments,
            MissingData = missingData,
            ProviderData = providerData
        };
    }

    private static RouteProviderData ToProviderData(ProviderRouteResult data)
    {
        return new RouteProviderData
        {
            Provider = data.Provider,
            DistanceMeters = data.DistanceMeters,
            ProviderRouteTimeSeconds = data.ProviderRouteTimeSeconds,
            RoadType = data.RoadType,
            SpeedLimitKmh = data.SpeedLimitKmh,
            TrafficSignalCount = data.TrafficSignalCount,
            IsAvailable = data.IsAvailable,
            MissingFields = data.MissingFields,
            Geometry = data.Geometry,
            Segments = data.Segments
        };
    }

    private void RecordProviderFailure(
        IRouteProvider provider,
        ICollection<RouteProviderData> providerData,
        string errorCode,
        Exception exception)
    {
        logger.LogWarning(exception, "Route provider {Provider} was unavailable: {ErrorCode}.", provider.Name, errorCode);
        providerData.Add(new RouteProviderData
        {
            Provider = provider.Name,
            IsAvailable = false,
            MissingFields = ["provider"]
        });
    }

    /// <summary>
    /// Splits long route segments into smaller sub-segments under <paramref name="maxSegmentLengthMeters"/> (default 200m).
    /// Scaled duration and length proportionally while preserving road type, speed limit, lanes, and OSM way IDs.
    /// </summary>
    public static IReadOnlyList<RouteSegment> SplitSegments(
        IReadOnlyList<RouteSegment> segments,
        double maxSegmentLengthMeters = 200.0)
    {
        if (segments.Count == 0)
            return segments;

        var result = new List<RouteSegment>();
        var sequence = 0;

        foreach (var segment in segments)
        {
            var length = segment.LengthMeters ?? 0;
            if (length <= maxSegmentLengthMeters || length <= 0)
            {
                result.Add(segment with { Sequence = sequence++ });
                continue;
            }

            var partsCount = (int)Math.Ceiling(length / maxSegmentLengthMeters);
            var subLength = length / partsCount;
            var subDuration = segment.ProviderSegmentTimeSeconds.HasValue
                ? segment.ProviderSegmentTimeSeconds.Value / partsCount
                : (double?)null;

            var points = segment.Geometry;

            for (var i = 0; i < partsCount; i++)
            {
                IReadOnlyList<RoutePoint> subGeometry = [];
                if (points.Count > 0)
                {
                    var startIdx = (int)Math.Floor((double)i * points.Count / partsCount);
                    var endIdx = (int)Math.Ceiling((double)(i + 1) * points.Count / partsCount);
                    endIdx = Math.Min(endIdx, points.Count);
                    subGeometry = points.Skip(startIdx).Take(Math.Max(1, endIdx - startIdx)).ToArray();
                }

                result.Add(new RouteSegment
                {
                    Sequence = sequence++,
                    LengthMeters = subLength,
                    ProviderSegmentTimeSeconds = subDuration,
                    OsmWayId = segment.OsmWayId,
                    RoadType = segment.RoadType,
                    SpeedLimitKmh = segment.SpeedLimitKmh,
                    LaneCount = segment.LaneCount,
                    IsRoundabout = i == 0 ? segment.IsRoundabout : false,
                    HasTrafficSignal = i == partsCount - 1 ? segment.HasTrafficSignal : false,
                    IsIntersection = i == 0 || i == partsCount - 1 ? segment.IsIntersection : false,
                    IsBridge = segment.IsBridge,
                    IsTunnel = segment.IsTunnel,
                    Geometry = subGeometry,
                    NodeIds = segment.NodeIds,
                    MissingData = segment.MissingData
                });
            }
        }

        return result;
    }
}
