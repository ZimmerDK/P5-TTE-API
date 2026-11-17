using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimeEstimationAPI.Models;
using TimeEstimationAPI.Services;
using Xunit;

[Trait("Category", "Provider")]
[Trait("Category", "Integration")]
[Trait("Category", "Live")]
public sealed class LiveProviderTests
{
    [Fact]
    public async Task Public_osrm_returns_a_route_for_known_coordinates()
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "TimeEstimationAPI.LiveTests/1.0 (local opt-in)");
        var service = new OsrmService(
            client,
            Options.Create(new OsmOptions
            {
                RoutingUrl = "https://router.project-osrm.org/route/v1/driving"
            }),
            new FixedGeocodingService(
                new RoutePoint(10.2039, 56.1629),
                new RoutePoint(10.2039, 56.1700)));

        var result = await service.GetRouteMetadataAsync("synthetic source", "synthetic destination", null);

        Assert.True(result.DistanceMeters > 0);
        Assert.True(result.ProviderRouteTimeSeconds > 0);
        Assert.NotEmpty(result.Segments);
    }

    [Fact]
    public async Task Public_nominatim_returns_coordinates_for_a_known_place()
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TimeEstimationAPI.Tests/1.0 (local opt-in)");
        var service = new GeocodingService(client, NullLogger<GeocodingService>.Instance);

        var point = await service.GeocodeAsync(
            "https://nominatim.openstreetmap.org/search",
            "Aalborg, Denmark");

        Assert.InRange(point.Latitude, 57.0, 57.1);
        Assert.InRange(point.Longitude, 9.7, 10.2);
    }

    private sealed class FixedGeocodingService(
        RoutePoint source,
        RoutePoint destination) : IGeocodingService
    {
        public Task<RoutePoint> GeocodeAsync(
            string geocodingUrl,
            string query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(query == "synthetic source" ? source : destination);

        public IReadOnlyList<string> GenerateCandidateQueries(string rawQuery) => [rawQuery];
    }
}
