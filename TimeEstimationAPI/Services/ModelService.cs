using TimeEstimationAPI.Models;

namespace TimeEstimationAPI.Services;

/// <summary>Runs route collection, feature mapping, and time prediction.</summary>
public sealed class ModelService
{
    private readonly RouteDataCollector _collector;
    private readonly FeatureTransformer _featureTransformer;
    private readonly ITravelTimeModelRunner _modelRunner;

    public ModelService(
        RouteDataCollector collector,
        FeatureTransformer featureTransformer,
        ITravelTimeModelRunner modelRunner)
    {
        _collector = collector;
        _featureTransformer = featureTransformer;
        _modelRunner = modelRunner;
    }

    /// <summary>
    /// Estimates a supplied segment route, or obtains one from a provider when
    /// the request only contains source and destination.
    /// </summary>
    public async Task<RouteResponse> EstimateAsync(
        RouteRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(request.Mode, "car", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only the 'car' travel mode is currently supported.");

        var route = request.Segments.Count > 0
            ? CreateRouteFromSegments(request)
            : await _collector.CollectAsync(request, cancellationToken);

        var vector = _featureTransformer.Transform(route);
        var prediction = await _modelRunner.PredictAsync(vector, cancellationToken);
        if (prediction.SegmentPredictions.Count != route.Segments.Count)
        {
            throw new ModelServiceUnavailableException(
                $"The model returned {prediction.SegmentPredictions.Count} segment predictions for {route.Segments.Count} route segments.");
        }

        var segmentPredictions = prediction.SegmentPredictions
            .Select(value => value ?? throw new ModelServiceUnavailableException(
                "The model returned a missing segment prediction."))
            .ToArray();
        route = route with
        {
            Segments = route.Segments.Select((segment, index) => segment with
            {
                PredictedDurationSeconds = segmentPredictions[index]
            }).ToArray()
        };

        return new RouteResponse
        {
            EstimatedTravelTimeSeconds = segmentPredictions.Sum(),
            ProviderEstimatedTravelTimeSeconds = route.ProviderEstimatedTravelTimeSeconds,
            ProviderName = route.SelectedProvider,
            DistanceMeters = route.DistanceMeters ?? 0,
            ModelName = vector.ModelName,
            Route = route,
            Features = new Dictionary<string, object>
            {
                ["distanceMeters"] = route.DistanceMeters ?? 0,
                ["viaCount"] = route.Via.Count,
                ["segmentCount"] = route.Segments.Count,
                ["departureTime"] = request.DepartureTime,
                ["source"] = request.Source ?? string.Empty,
                ["destination"] = request.Destination ?? string.Empty,
                ["missingData"] = route.MissingData.Count > 0,
                ["missingDataFields"] = route.MissingData
            }
        };
    }

    private static UnifiedRoute CreateRouteFromSegments(RouteRequest request)
    {
        var segments = request.Segments.Select((segment, index) => new RouteSegment
        {
            Sequence = index,
            LengthMeters = segment.LengthMeters,
            RoadType = segment.RoadType,
            SpeedLimitKmh = segment.SpeedLimitKmh,
            LaneCount = segment.LaneCount,
            IsRoundabout = segment.IsRoundabout,
            HasTrafficSignal = segment.HasTrafficSignal,
            IsIntersection = segment.IsIntersection,
            MissingData = MissingFields(segment)
        }).ToArray();

        return new UnifiedRoute
        {
            Source = request.Source ?? string.Empty,
            Destination = request.Destination ?? string.Empty,
            Via = request.Via,
            DepartureTime = request.DepartureTime,
            Mode = request.Mode,
            DistanceMeters = segments.Sum(segment => segment.LengthMeters ?? 0),
            ProviderEstimatedTravelTimeSeconds = null,
            Segments = segments,
            MissingData = segments
                .SelectMany(segment => segment.MissingData)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static IReadOnlyCollection<string> MissingFields(RouteSegmentInput segment)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(segment.RoadType)) missing.Add("roadType");
        if (!segment.SpeedLimitKmh.HasValue) missing.Add("speedLimitKmh");
        if (!segment.LaneCount.HasValue) missing.Add("laneCount");
        if (!segment.IsRoundabout.HasValue) missing.Add("isRoundabout");
        if (!segment.HasTrafficSignal.HasValue) missing.Add("hasTrafficSignal");
        if (!segment.IsIntersection.HasValue) missing.Add("isIntersection");
        return missing;
    }
}
