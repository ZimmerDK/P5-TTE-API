using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

[Trait("Category", "E2E")]
[Trait("Category", "Integration")]
public sealed class LocalServicesE2ETests
{
    private readonly HttpClient _client = new()
    {
        BaseAddress = new Uri(
            Environment.GetEnvironmentVariable("API_E2E_URL") ?? "http://localhost:5073"),
        Timeout = TimeSpan.FromSeconds(45)
    };

    [Fact]
    public async Task Running_local_services_report_health()
    {
        using var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Running_local_services_estimate_explicit_segments()
    {
        using var response = await _client.PostAsJsonAsync("/api/routes/model-only", new
        {
            segments = new[]
            {
                new
                {
                    lengthMeters = 1000,
                    roadType = "primary",
                    speedLimitKmh = 50,
                    laneCount = 2,
                    isRoundabout = false,
                    hasTrafficSignal = false,
                    isIntersection = false,
                    isBridge = false,
                    isTunnel = false
                }
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.True(root.GetProperty("modelRouteTimeSeconds").GetDouble() >= 0);
        Assert.Equal(1, root.GetProperty("route").GetProperty("segments").GetArrayLength());
    }

    [Fact]
    public async Task Running_local_services_estimate_development_provider_route()
    {
        using var response = await _client.PostAsJsonAsync("/api/routes/estimate", new
        {
            source = "synthetic source",
            destination = "synthetic destination"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("development-stub", root.GetProperty("routeProvider").GetString());
        Assert.True(root.GetProperty("modelRouteTimeSeconds").GetDouble() >= 0);
    }
}
