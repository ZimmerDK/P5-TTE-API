using TimeEstimationAPI.Models;

namespace TimeEstimationAPI.Services;

public interface IModelService
{
    Task<RouteResponse> EstimateAsync(RouteRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Runs the route collection, feature mapping, model, and benchmark steps.</summary>
public sealed class ModelService : IModelService
{
    private readonly RouteDataCollector _collector;
    private readonly FeatureTransformer _featureTransformer;
    private readonly TravelTimeModelRunner _modelRunner;
    private readonly BenchmarkEvaluator _benchmarkEvaluator;
    private readonly IGoogleRoutesService _googleRoutesService;

    public ModelService(
        RouteDataCollector collector,
        FeatureTransformer featureTransformer,
        TravelTimeModelRunner modelRunner,
        BenchmarkEvaluator benchmarkEvaluator,
        IGoogleRoutesService googleRoutesService)
    {
        _collector = collector;
        _featureTransformer = featureTransformer;
        _modelRunner = modelRunner;
        _benchmarkEvaluator = benchmarkEvaluator;
        _googleRoutesService = googleRoutesService;
    }

    /// <summary>Returns an estimate together with the route data used to make it.</summary>
    public async Task<RouteResponse> EstimateAsync(RouteRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Waypoints.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Waypoints must not be empty.");
        }

        if (!string.Equals(request.Mode, "car", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only the 'car' travel mode is currently supported.");
        }

        var route = await _collector.CollectAsync(request, cancellationToken);
        var vector = _featureTransformer.Transform(route);
        var predictedSeconds = _modelRunner.Predict(vector);
        var googleSeconds = await _googleRoutesService.GetDurationSecondsAsync(
            request.Source, request.Destination, request.Waypoints, cancellationToken);
        var benchmark = _benchmarkEvaluator.Evaluate(predictedSeconds, route.ProviderData, googleSeconds);

        return new RouteResponse
        {
            EstimatedTravelTimeSeconds = predictedSeconds,
            DistanceMeters = route.DistanceMeters ?? 0,
            Confidence = 0.82,
            ModelName = vector.ModelName,
            BenchmarkComparison = benchmark,
            Route = route,
            Features = new Dictionary<string, object>
            {
                ["roadType"] = route.RoadType ?? "unknown",
                ["speedLimitKmh"] = route.SpeedLimitKmh ?? 0,
                ["distanceMeters"] = route.DistanceMeters ?? 0,
                ["waypointsCount"] = route.Waypoints.Count,
                ["departureTime"] = request.DepartureTime,
                ["source"] = request.Source,
                ["destination"] = request.Destination,
                ["missingData"] = route.MissingData.Count > 0,
                ["missingDataFields"] = route.MissingData
            }
        };
    }
}
