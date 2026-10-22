using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TimeEstimationAPI.Models;

namespace TimeEstimationAPI.Services;

public sealed class PythonModelRunner(
    HttpClient httpClient,
    IOptions<RouteModelOptions> options,
    ILogger<PythonModelRunner> logger) : ITravelTimeModelRunner
{
    private readonly HttpClient _httpClient = ConfigureClient(httpClient, options.Value);

    public async Task<ModelPrediction> PredictAsync(
        RouteFeatureVector featureVector,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(featureVector, cancellationToken);
        return new ModelPrediction(
            response.ModelRouteTimeSeconds,
            response.ModelSegmentTimes);
    }

    private async Task<PythonPredictionResponse> SendAsync(
        RouteFeatureVector featureVector,
        CancellationToken cancellationToken)
    {
        var request = new PythonPredictionRequest(
            featureVector.ModelName,
            featureVector.RouteFeatures,
            featureVector.SegmentFeatures,
            new Dictionary<string, object?>
            {
                ["sinHour"] = featureVector.TimeFeatures.ElementAtOrDefault(0),
                ["cosHour"] = featureVector.TimeFeatures.ElementAtOrDefault(1),
                ["sinMinute"] = featureVector.TimeFeatures.ElementAtOrDefault(2),
                ["cosMinute"] = featureVector.TimeFeatures.ElementAtOrDefault(3)
            });

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync(
                "predict", request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new ModelServiceUnavailableException(
                "The Python model service could not be reached.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelServiceUnavailableException(
                "The Python model service timed out.", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError(
                    "Python model service returned HTTP {StatusCode}: {ResponseBody}",
                    (int)response.StatusCode, body);
                throw new ModelServiceUnavailableException(
                    $"Python model service returned HTTP {(int)response.StatusCode}.");
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<PythonPredictionResponse>(
                    cancellationToken)
                    ?? throw new ModelServiceUnavailableException(
                        "Python model service returned an empty prediction response.");
            }
            catch (JsonException exception)
            {
                throw new ModelServiceUnavailableException(
                    "Python model service returned an invalid prediction response.", exception);
            }
        }
    }

    private static HttpClient ConfigureClient(HttpClient client, RouteModelOptions options)
    {
        client.BaseAddress = new Uri(options.PythonServiceUrl.TrimEnd('/') + "/", UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(options.PythonServiceTimeoutSeconds, 1));
        return client;
    }

    private sealed record PythonPredictionRequest(
        string ModelName,
        IReadOnlyDictionary<string, object?> RouteFeatures,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> SegmentFeatures,
        IReadOnlyDictionary<string, object?> TimeFeatures);

    private sealed record PythonPredictionResponse(
        [property: JsonPropertyName("estimated_travel_time_seconds")]
        double ModelRouteTimeSeconds,
        [property: JsonPropertyName("segment_predictions")]
        IReadOnlyList<double?> ModelSegmentTimes);
}

public sealed class ModelServiceUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
