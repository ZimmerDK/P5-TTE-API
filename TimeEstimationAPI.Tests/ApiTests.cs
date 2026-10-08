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
            waypoints = new[] { "C", "D" },
            departureTime = "2026-09-24T08:30:00Z",
            mode = "car"
        });

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, document.RootElement.GetProperty("features").GetProperty("waypointsCount").GetInt32());
    }

    [Fact]
    public async Task Estimate_returns_unified_route_and_placeholder_estimate()
    {
        var response = await _client.PostAsJsonAsync("/api/routes/estimate", new
        {
            source = "Stockholm",
            destination = "Gothenburg",
            waypoints = Array.Empty<string>(),
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
        Assert.Contains("durationSeconds", root.GetProperty("features")
            .GetProperty("missingDataFields").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task Estimate_rejects_missing_endpoints()
    {
        var response = await _client.PostAsJsonAsync("/api/routes/estimate", new
        {
            source = "",
            destination = "",
            waypoints = Array.Empty<string>(),
            departureTime = "2026-09-24T08:30:00Z",
            mode = "car"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
