using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Extensions.Options;
using TimeEstimationAPI.Models;

namespace TimeEstimationAPI.Services;

/// <summary>Settings for the trained route model and its local fallback.</summary>
public sealed class RouteModelOptions
{
    public bool UsePlaceholder { get; set; }
    public string ModelPath { get; set; } = "best_model.onnx";
    public double PlaceholderAverageSpeedKmh { get; set; } = 40;
}

/// <summary>Loads the ONNX model and turns route features into a time estimate.</summary>
public sealed class TravelTimeModelRunner
{
    private const int TimeFeatureCount = 4;
    private readonly InferenceSession? _session;
    private readonly int _sequenceLength;
    private readonly int _sequenceFeatureCount;
    private readonly RouteModelOptions _options;

    /// <summary>Loads the configured model when available, otherwise keeps the fallback.</summary>
    public TravelTimeModelRunner(
        IOptions<RouteModelOptions> options,
        ILogger<TravelTimeModelRunner> logger)
    {
        _options = options.Value;
        var modelPath = Path.IsPathRooted(options.Value.ModelPath)
            ? options.Value.ModelPath
            : Path.Combine(AppContext.BaseDirectory, options.Value.ModelPath);

        if (options.Value.UsePlaceholder)
        {
            logger.LogWarning("Placeholder route model is enabled; ONNX inference is disabled.");
            return;
        }

        if (!File.Exists(modelPath))
        {
            logger.LogWarning(
                "Route model file {ModelPath} was not found. Using the placeholder model until a trained model is supplied.",
                modelPath);
            return;
        }

        _session = new InferenceSession(modelPath);
        var sequenceMetadata = _session.InputMetadata["sequence"];
        if (sequenceMetadata.Dimensions.Length != 3 ||
            sequenceMetadata.Dimensions[1] <= 0 ||
            sequenceMetadata.Dimensions[2] <= 0)
        {
            throw new InvalidOperationException("The ONNX model must expose a fixed rank-3 'sequence' input.");
        }

        _sequenceLength = checked((int)sequenceMetadata.Dimensions[1]);
        _sequenceFeatureCount = checked((int)sequenceMetadata.Dimensions[2]);
    }

    /// <summary>Runs ONNX inference, or simple fallback while no model is loaded.</summary>
    public double Predict(RouteFeatureVector featureVector)
    {
        if (featureVector.Sequence.Length == 0)
        {
            throw new ArgumentException("Feature vector cannot be empty.");
        }

        if (featureVector.TimeFeatures.Length != TimeFeatureCount)
        {
            throw new ArgumentException($"Time features must contain exactly {TimeFeatureCount} values.");
        }

        if (_session is null)
        {
            return PredictWithPlaceholder(featureVector, _options);
        }

        var featureTensor = new DenseTensor<float>(new[] { 1, _sequenceLength, _sequenceFeatureCount });
        for (var row = 0; row < featureVector.Sequence.Length && row < _sequenceLength; row++)
        {
            for (var column = 0;
                 column < featureVector.Sequence[row].Length && column < _sequenceFeatureCount;
                 column++)
                featureTensor[0, row, column] = (float)featureVector.Sequence[row][column];
        }

        var timeTensor = new DenseTensor<float>(new[] { 1, TimeFeatureCount });
        for (var i = 0; i < TimeFeatureCount; i++)
        {
            timeTensor[0, i] = (float)featureVector.TimeFeatures[i];
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("sequence", featureTensor),
            NamedOnnxValue.CreateFromTensor("time_features", timeTensor)
        };

        using var results = _session.Run(inputs);
        var output = results.First().AsTensor<float>().ToArray();
        return output.Length > 0 ? output[0] : 0d;
    }

    // Just enough to keep the full API flow usable before the real model arrives.
    private static double PredictWithPlaceholder(
        RouteFeatureVector featureVector,
        RouteModelOptions options)
    {
        // This is a transparent baseline, not a trained prediction.
        var totalSeconds = 0d;
        foreach (var segment in featureVector.Sequence)
        {
            var distanceKm = Math.Max(segment.ElementAtOrDefault(0), 0);
            var speedKmh = segment.ElementAtOrDefault(1) * 130;
            speedKmh = speedKmh > 0 ? speedKmh : Math.Max(options.PlaceholderAverageSpeedKmh, 1);
            var signalDelaySeconds = segment.ElementAtOrDefault(5) * 10;
            var roundaboutDelaySeconds = segment.ElementAtOrDefault(4) * 5;
            totalSeconds += distanceKm / speedKmh * 3600 + signalDelaySeconds + roundaboutDelaySeconds;
        }

        return totalSeconds;
    }
}
