namespace TimeEstimationAPI.Services;

using TimeEstimationAPI.Models;
using System.Text.Json;

/// <summary>Gets provider data and folds it into one route object for the model.</summary>
public sealed class RouteDataCollector(
    IEnumerable<IRouteProvider> providers,
    ILogger<RouteDataCollector> logger)
{
    /// <summary>Collects route data and records which fields were not available.</summary>
    public async Task<UnifiedRoute> CollectAsync(RouteRequest request, CancellationToken cancellationToken)
    {
        var providerData = new List<RouteProviderData>();
        foreach (var provider in providers)
        {
            try
            {
                var data = await provider.GetRouteMetadataAsync(
                    request.Source, request.Destination, request.Waypoints, cancellationToken);
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
        if (!primary.DurationSeconds.HasValue) missingData.Add("durationSeconds");
        if (!primary.SpeedLimitKmh.HasValue) missingData.Add("speedLimitKmh");
        if (string.IsNullOrWhiteSpace(primary.RoadType)) missingData.Add("roadType");

        return new UnifiedRoute
        {
            Source = request.Source,
            Destination = request.Destination,
            Waypoints = request.Waypoints,
            DepartureTime = request.DepartureTime,
            Mode = request.Mode,
            DistanceMeters = primary.DistanceMeters,
            RoadType = primary.RoadType,
            SpeedLimitKmh = primary.SpeedLimitKmh,
            TrafficSignalCount = primary.TrafficSignalCount,
            RouteDurationSeconds = primary.DurationSeconds,
            MissingData = missingData,
            ProviderData = providerData
        };
    }

    private static RouteProviderData ToProviderData(OsmRouteMetadata data)
    {
        return new RouteProviderData
        {
            Provider = data.Provider,
            DistanceMeters = data.DistanceMeters,
            DurationSeconds = data.DurationSeconds,
            RoadType = data.RoadType,
            SpeedLimitKmh = data.SpeedLimitKmh,
            TrafficSignalCount = data.TrafficSignalCount,
            IsAvailable = data.IsAvailable,
            MissingFields = data.MissingFields,
            Raw = data.Raw
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
            MissingFields = ["provider"],
            Raw = new Dictionary<string, object> { ["error"] = errorCode }
        });
    }
}
