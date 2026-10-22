using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

public sealed class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_returns_ok()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", await response.Content.ReadAsStringAsync());
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
            mode = "car"
        });

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, document.RootElement.GetProperty("features").GetProperty("viaCount").GetInt32());
        Assert.Equal(3, document.RootElement.GetProperty("route").GetProperty("segments").GetArrayLength());
    }

    [Fact]
    public async Task Estimate_returns_unified_route_and_placeholder_estimate()
    {
        var response = await _client.PostAsJsonAsync("/api/routes/estimate", new
        {
            source = "Stockholm",
            destination = "Gothenburg",
            via = Array.Empty<string>(),
            departureTime = "2026-10-08T12:00:00Z",
            mode = "car"
        });

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.True(root.GetProperty("estimatedTravelTimeSeconds").GetDouble() > 0);
        Assert.Equal("Stockholm", root.GetProperty("route").GetProperty("source").GetString());
        Assert.Equal("development-stub", root.GetProperty("route")
            .GetProperty("providerData")[0].GetProperty("provider").GetString());
        Assert.True(root.GetProperty("route").GetProperty("segments").GetArrayLength() > 0);
        Assert.Contains("providerEstimatedDurationSeconds", root.GetProperty("features")
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
            mode = "car"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
            mode = "car"
        });

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("route").GetProperty("segments").GetArrayLength());
        Assert.All(root.GetProperty("route").GetProperty("segments").EnumerateArray(),
            segment => Assert.True(segment.GetProperty("predictedDurationSeconds").GetDouble() > 0));
        var total = root.GetProperty("estimatedTravelTimeSeconds").GetDouble();
        var segmentTotal = root.GetProperty("route").GetProperty("segments").EnumerateArray()
            .Sum(segment => segment.GetProperty("predictedDurationSeconds").GetDouble());
        Assert.Equal(segmentTotal, total, precision: 10);
    }
}
