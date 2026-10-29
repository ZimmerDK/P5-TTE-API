using TimeEstimationAPI.Models;

namespace TimeEstimationAPI.Services;

/// <summary>Runs route collection, feature mapping, and time prediction.</summary>
public sealed class ModelService
{
    private readonly RouteDataCollector _collector;
    private readonly FeatureTransformer _featureTransformer;
    private readonly ITravelTimeModelRunner _modelRunner;
    private readonly TrafficContextEnricher _contextEnricher;

    public ModelService(
        RouteDataCollector collector,
        FeatureTransformer featureTransformer,
        ITravelTimeModelRunner modelRunner,
        TrafficContextEnricher contextEnricher)
    {
        _collector = collector;
        _featureTransformer = featureTransformer;
        _modelRunner = modelRunner;
        _contextEnricher = contextEnricher;
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

        if (request.Segments.Count > 0)
        {
            var enrichedSegments = await _contextEnricher.EnrichAsync(route.Segments, cancellationToken);
            route = route with { Segments = enrichedSegments };
        }

        var vector = _featureTransformer.Transform(route);
        var prediction = await _modelRunner.PredictAsync(vector, cancellationToken);
        if (prediction.ModelSegmentTimes.Count != route.Segments.Count)
        {
            throw new ModelServiceUnavailableException(
                $"The model returned {prediction.ModelSegmentTimes.Count} segment predictions for {route.Segments.Count} route segments.");
        }

        var segmentPredictions = prediction.ModelSegmentTimes
            .Select(value => value ?? throw new ModelServiceUnavailableException(
                "The model returned a missing segment prediction."))
            .ToArray();
        route = route with
        {
            Segments = route.Segments.Select((segment, index) => segment with
            {
                ModelSegmentTimeSeconds = segmentPredictions[index]
            }).ToArray()
        };

        return new RouteResponse
        {
            ModelRouteTimeSeconds = segmentPredictions.Sum(),
            ProviderRouteTimeSeconds = route.ProviderRouteTimeSeconds,
            RouteProvider = route.SelectedProvider,
            RouteDistanceM = route.DistanceMeters ?? 0,
            ModelName = prediction.ModelName,
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

    /// <summary>Runs only the configured model for an explicit segment route.</summary>
    public Task<RouteResponse> EstimateSegmentsAsync(
        RouteRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Segments.Count == 0)
            throw new ArgumentException("At least one segment is required for model-only estimation.");

        return EstimateAsync(request, cancellationToken);
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
            IsBridge = segment.IsBridge,
            IsTunnel = segment.IsTunnel,
            Density = segment.Density ?? segment.RoadDensity ?? segment.PopulationDensity,
            RoadDensity = segment.RoadDensity,
            PopulationDensity = segment.PopulationDensity,
            BuiltVolumeDensity = segment.BuiltVolumeDensity,
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
            ProviderRouteTimeSeconds = null,
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
        if (!segment.IsBridge.HasValue) missing.Add("isBridge");
        if (!segment.IsTunnel.HasValue) missing.Add("isTunnel");
        if (!segment.Density.HasValue && !segment.RoadDensity.HasValue && !segment.PopulationDensity.HasValue) missing.Add("density");
        return missing;
    }
}
