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
}
