using TimeEstimationAPI.Models;

namespace TimeEstimationAPI.Services;

/// <summary>Settings for the configured model service.</summary>
public sealed class RouteModelOptions
{
    public string PythonServiceUrl { get; set; } = "http://localhost:8001";
    public int PythonServiceTimeoutSeconds { get; set; } = 30;
}

public sealed record ModelPrediction(
    double ModelRouteTimeSeconds,
    IReadOnlyList<double?> ModelSegmentTimes,
    string ModelName = "route_time_model_v1");

public interface ITravelTimeModelRunner
{
    Task<ModelPrediction> PredictAsync(
        RouteFeatureVector featureVector,
        CancellationToken cancellationToken);
}
