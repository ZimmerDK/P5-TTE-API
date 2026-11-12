using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using TimeEstimationAPI.Models;
using TimeEstimationAPI.Services;
using Xunit;

public sealed class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ITravelTimeModelRunner>();
                services.AddSingleton<ITravelTimeModelRunner, TestModelRunner>();
            });
        }).CreateClient();
    }

    [Fact]
    public async Task Health_returns_ok()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Estimate_counts_waypoints_from_request()
    {
        var response = await _client.PostAsJsonAsync("/api/routes/estimate", new
        {
            source = "A",
            destination = "B",
            via = new[] { "C", "D" },
            departureTime = "2026-09-24T08:30:00Z",
        });

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, document.RootElement.GetProperty("features").GetProperty("viaCount").GetInt32());
        Assert.True(document.RootElement.GetProperty("route").GetProperty("segments").GetArrayLength() >= 3);
    }

    [Fact]
    public async Task Estimate_returns_unified_route_and_model_estimate()
    {
        var response = await _client.PostAsJsonAsync("/api/routes/estimate", new
        {
            source = "Stockholm",
            destination = "Gothenburg",
            via = Array.Empty<string>(),
            departureTime = "2026-10-08T12:00:00Z",
        });

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.True(root.GetProperty("modelRouteTimeSeconds").GetDouble() > 0);
        Assert.Equal("Stockholm", root.GetProperty("route").GetProperty("source").GetString());
        Assert.Equal("development-stub", root.GetProperty("route")
            .GetProperty("providerData")[0].GetProperty("provider").GetString());
        Assert.True(root.GetProperty("route").GetProperty("segments").GetArrayLength() > 0);
        Assert.Contains("providerRouteTimeSeconds", root.GetProperty("features")
            .GetProperty("missingDataFields").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task Estimate_rejects_missing_endpoints()
    {
        var response = await _client.PostAsJsonAsync("/api/routes/estimate", new
        {
            source = "",
            destination = "",
            via = Array.Empty<string>(),
            departureTime = "2026-09-24T08:30:00Z",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void RouteRequest_reads_per_request_google_opt_out()
    {
        var request = JsonSerializer.Deserialize<RouteRequest>(
            """{"source":"A","destination":"B","useGoogleMaps":false}""");

        Assert.NotNull(request);
        Assert.False(request!.UseGoogleMaps);
    }

    [Fact]
    public async Task Estimate_accepts_an_ordered_segment_route()
    {
        var response = await _client.PostAsJsonAsync("/api/routes/estimate", new
        {
            segments = new[]
            {
                new
                {
                    lengthMeters = 1000,
                    speedLimitKmh = 50,
                    laneCount = 2,
                    isRoundabout = false,
                    hasTrafficSignal = true,
                    isIntersection = true
                },
                new
                {
                    lengthMeters = 500,
                    speedLimitKmh = 30,
                    laneCount = 1,
                    isRoundabout = true,
                    hasTrafficSignal = false,
                    isIntersection = true
                }
            },
            departureTime = "2026-09-24T08:30:00Z",
        });

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("route").GetProperty("segments").GetArrayLength());
        Assert.All(root.GetProperty("route").GetProperty("segments").EnumerateArray(),
            segment => Assert.True(segment.GetProperty("modelSegmentTimeSeconds").GetDouble() > 0));
        var total = root.GetProperty("modelRouteTimeSeconds").GetDouble();
        var segmentTotal = root.GetProperty("route").GetProperty("segments").EnumerateArray()
            .Sum(segment => segment.GetProperty("modelSegmentTimeSeconds").GetDouble());
        Assert.Equal(segmentTotal, total, precision: 10);
    }

    [Fact]
    public async Task Provider_data_returns_route_without_model_prediction()
    {
        var response = await _client.PostAsJsonAsync("/api/routes/provider-data", new
        {
            source = "Stockholm",
            destination = "Gothenburg",
            via = Array.Empty<string>(),
            departureTime = "2026-09-24T08:30:00Z",
        });

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.True(root.GetProperty("segments").GetArrayLength() > 0);
        Assert.False(root.TryGetProperty("modelRouteTimeSeconds", out _));
    }

    [Fact]
    public async Task Model_estimate_accepts_explicit_segments_without_provider_routing()
    {
        var response = await _client.PostAsJsonAsync("/api/routes/model-only", new
        {
            segments = new[]
            {
                new
                {
                    lengthMeters = 1000,
                    roadType = "residential",
                    speedLimitKmh = 50,
                    laneCount = 2,
                    isRoundabout = false,
                    hasTrafficSignal = true,
                    isIntersection = true,
                    isBridge = false,
                    isTunnel = false
                }
            },
            departureTime = "2026-09-24T08:30:00Z",
        });

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.True(root.GetProperty("modelRouteTimeSeconds").GetDouble() > 0);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("providerRouteTimeSeconds").ValueKind);
    }

    [Fact]
    public void GeocodingService_generates_correct_fallback_candidates()
    {
        var service = new GeocodingService(new HttpClient(), NullLogger<GeocodingService>.Instance);

        var candidates1 = service.GenerateCandidateQueries("Salling Aarhus, Søndergade 27, 8000 Aarhus");
        Assert.Equal("Salling Aarhus, Søndergade 27, 8000 Aarhus", candidates1[0]);
        Assert.Contains("Søndergade 27, 8000 Aarhus", candidates1);
        Assert.True(
            Array.IndexOf(candidates1.ToArray(), "Søndergade 27, 8000 Aarhus")
            < Array.IndexOf(candidates1.ToArray(), "Salling Aarhus, Søndergade 27"));
        Assert.Contains("Salling Aarhus, 8000 Aarhus", candidates1);
        Assert.DoesNotContain("8000 Aarhus", candidates1);

        var candidates2 = service.GenerateCandidateQueries("Lynge Kirke, Lynge Bygade 18B, 3540 Lynge");
        Assert.Equal("Lynge Kirke, Lynge Bygade 18B, 3540 Lynge", candidates2[0]);
        Assert.Contains("Lynge Bygade 18B, 3540 Lynge", candidates2);
        Assert.Contains("Lynge Kirke, 3540 Lynge", candidates2);
        Assert.DoesNotContain("3540 Lynge", candidates2);

        var candidates3 = service.GenerateCandidateQueries("Colosseum, Piazza del Colosseo 1, 00184 Roma RM, Italy");
        Assert.Equal("Colosseum, Piazza del Colosseo 1, 00184 Roma RM, Italy", candidates3[0]);
        Assert.Contains("Piazza del Colosseo 1, 00184 Roma RM, Italy", candidates3);

        // Address with floor/unit specifier (4.tv) and country
        var candidates4 = service.GenerateCandidateQueries("Thulevej 44, 4.tv, 9210 Aalborg SØ, Denmark");
        Assert.Equal("Thulevej 44, 4.tv, 9210 Aalborg SØ, Denmark", candidates4[0]);
        Assert.Contains("Thulevej 44, 9210 Aalborg SØ, Denmark", candidates4);
        Assert.Contains("Thulevej 44 4.tv, 9210 Aalborg SØ, Denmark", candidates4);
        Assert.Contains("Thulevej 44, 9210 Aalborg SØ", candidates4);
        // Must NOT drop street name to fall back to postal code, city, or country
        Assert.DoesNotContain("9210 Aalborg SØ, Denmark", candidates4);
        Assert.DoesNotContain("9210 Aalborg SØ", candidates4);
        Assert.DoesNotContain("Denmark", candidates4);

        // US address with suite and country
        var candidates5 = service.GenerateCandidateQueries("100 Main Street, Apt 4B, New York, NY 10001, USA");
        Assert.Contains("100 Main Street, New York, NY 10001, USA", candidates5);
        Assert.DoesNotContain("New York, NY 10001, USA", candidates5);
        Assert.DoesNotContain("USA", candidates5);
    }

    private sealed class TestModelRunner : ITravelTimeModelRunner
    {
        public Task<ModelPrediction> PredictAsync(
            RouteFeatureVector featureVector,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ModelPrediction(
                featureVector.Sequence.Length,
                Enumerable.Repeat<double?>(1, featureVector.Sequence.Length).ToArray(),
                "test-model"));
    }
}
