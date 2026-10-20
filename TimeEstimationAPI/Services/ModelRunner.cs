using Microsoft.Extensions.Options;
using TimeEstimationAPI.Models;

namespace TimeEstimationAPI.Services;

/// <summary>Settings for the Python model service and local fallback.</summary>
public sealed class RouteModelOptions
{
    public bool UsePlaceholder { get; set; }
    public bool UsePythonService { get; set; }
    public string PythonServiceUrl { get; set; } = "http://localhost:8001";
    public int PythonServiceTimeoutSeconds { get; set; } = 30;
    public double PlaceholderAverageSpeedKmh { get; set; } = 40;
}

public sealed record ModelPrediction(
    double EstimatedTravelTimeSeconds,
    IReadOnlyList<double?> SegmentPredictions);

public interface ITravelTimeModelRunner
{
    Task<ModelPrediction> PredictAsync(
        RouteFeatureVector featureVector,
        CancellationToken cancellationToken);
}

/// <summary>Runs the local placeholder prediction until the Python service is enabled.</summary>
public sealed class TravelTimeModelRunner(
    IOptions<RouteModelOptions> options) : ITravelTimeModelRunner
{
    private readonly RouteModelOptions _options = options.Value;

    public Task<ModelPrediction> PredictAsync(
        RouteFeatureVector featureVector,
        CancellationToken cancellationToken)
    {
        if (featureVector.Sequence.Length == 0)
            throw new ArgumentException("Feature vector cannot be empty.");

        var segmentPredictions = PredictPlaceholderSegments(featureVector, _options)
            .Cast<double?>()
            .ToArray();
        return Task.FromResult(new ModelPrediction(
            segmentPredictions.Sum(value => value ?? 0),
            segmentPredictions));
    }

    private static double[] PredictPlaceholderSegments(
        RouteFeatureVector featureVector,
        RouteModelOptions options)
    {
        var predictions = new double[featureVector.Sequence.Length];
        for (var index = 0; index < featureVector.Sequence.Length; index++)
        {
            var segment = featureVector.Sequence[index];
            var distanceKm = Math.Max(segment.ElementAtOrDefault(0), 0);
            var speedKmh = segment.ElementAtOrDefault(1) * 130;
            speedKmh = speedKmh > 0 ? speedKmh : Math.Max(options.PlaceholderAverageSpeedKmh, 1);
            var signalDelaySeconds = segment.ElementAtOrDefault(5) * 10;
            var roundaboutDelaySeconds = segment.ElementAtOrDefault(4) * 5;
            predictions[index] = distanceKm / speedKmh * 3600
                + signalDelaySeconds
                + roundaboutDelaySeconds;
        }

        return predictions;
    }
}
