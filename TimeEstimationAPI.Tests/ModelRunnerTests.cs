using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimeEstimationAPI.Models;
using TimeEstimationAPI.Services;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ModelRunnerTests
{
    [Fact]
    public async Task PredictAsync_returns_prediction_and_model_name_from_service()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                estimated_travel_time_seconds = 487.0,
                segment_predictions = new double?[] { 67.0, 420.0 },
                model_name = "test-regressor"
            })
        }));
        var runner = CreateRunner(client);

        var result = await runner.PredictAsync(
            new RouteFeatureVector
            {
                SegmentFeatures =
                [
                    new Dictionary<string, object?> { ["length_m"] = 69 },
                    new Dictionary<string, object?> { ["length_m"] = 67 }
                ]
            },
            CancellationToken.None);

        Assert.Equal(487.0, result.ModelRouteTimeSeconds);
        Assert.Equal([67.0, 420.0], result.ModelSegmentTimes);
        Assert.Equal("test-regressor", result.ModelName);
    }

    [Fact]
    public async Task PredictAsync_maps_non_success_response_to_model_service_error()
    {
        using var client = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent("invalid model input")
            }));
        var runner = CreateRunner(client);

        var exception = await Assert.ThrowsAsync<ModelServiceUnavailableException>(() =>
            runner.PredictAsync(new RouteFeatureVector(), CancellationToken.None));

        Assert.Contains("returned HTTP 422", exception.Message);
    }

    [Fact]
    public async Task PredictAsync_maps_invalid_json_to_model_service_error()
    {
        using var client = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{not-json")
            }));
        var runner = CreateRunner(client);

        var exception = await Assert.ThrowsAsync<ModelServiceUnavailableException>(() =>
            runner.PredictAsync(new RouteFeatureVector(), CancellationToken.None));

        Assert.Contains("invalid prediction response", exception.Message);
    }

    [Fact]
    public async Task PredictAsync_maps_timeout_to_model_service_error()
    {
        using var client = new HttpClient(new StubHandler(_ =>
            throw new TaskCanceledException("simulated timeout")));
        var runner = CreateRunner(client);

        var exception = await Assert.ThrowsAsync<ModelServiceUnavailableException>(() =>
            runner.PredictAsync(new RouteFeatureVector(), CancellationToken.None));

        Assert.Contains("timed out", exception.Message);
    }

    private static PythonModelRunner CreateRunner(HttpClient client) =>
        new(
            client,
            Options.Create(new RouteModelOptions
            {
                PythonServiceUrl = "https://ml.test",
                PythonServiceTimeoutSeconds = 6
            }),
            NullLogger<PythonModelRunner>.Instance);

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
