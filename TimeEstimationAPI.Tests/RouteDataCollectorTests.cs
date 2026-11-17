using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimeEstimationAPI.Models;
using TimeEstimationAPI.Services;
using Xunit;

[Trait("Category", "Integration")]
public sealed class RouteDataCollectorTests
{
    [Fact]
    public async Task CollectAsync_uses_an_available_provider_after_another_fails()
    {
        var collector = CreateCollector(
            new FailingProvider("unavailable"),
            new SuccessfulProvider("working"));

        var route = await collector.CollectAsync(
            new RouteRequest { Source = "A", Destination = "B" },
            CancellationToken.None);

        Assert.Equal("working", route.SelectedProvider);
        Assert.Single(route.Segments);
        Assert.Equal(2, route.ProviderData.Count);
        Assert.False(route.ProviderData[0].IsAvailable);
        Assert.True(route.ProviderData[1].IsAvailable);
    }

    [Fact]
    public async Task CollectAsync_throws_when_all_providers_fail()
    {
        var collector = CreateCollector(new FailingProvider("first"), new FailingProvider("second"));

        var exception = await Assert.ThrowsAsync<RouteProviderUnavailableException>(() =>
            collector.CollectAsync(
                new RouteRequest { Source = "A", Destination = "B" },
                CancellationToken.None));

        Assert.Contains("No route provider", exception.Message);
    }

    private static RouteDataCollector CreateCollector(params IRouteProvider[] providers)
    {
        var osmTagEnricher = new OsmTagEnricher(
            new HttpClient(),
            Options.Create(new OsmOptions { UseOverpass = false }),
            NullLogger<OsmTagEnricher>.Instance,
            new MemoryCache(Options.Create(new MemoryCacheOptions())));
        var contextEnricher = new TrafficContextEnricher(
            new HttpClient(),
            Options.Create(new ContextOptions { Enabled = false }),
            NullLogger<TrafficContextEnricher>.Instance);

        return new RouteDataCollector(
            providers,
            NullLogger<RouteDataCollector>.Instance,
            osmTagEnricher,
            contextEnricher,
            new StubGeocodingService(),
            Options.Create(new OsmOptions()),
            Options.Create(new GoogleMapsOptions()));
    }

    private sealed class FailingProvider(string name) : IRouteProvider
    {
        public string Name => name;

        public Task<ProviderRouteResult> GetRouteMetadataAsync(
            string source,
            string destination,
            List<string>? waypoints,
            CancellationToken cancellationToken = default,
            IReadOnlyList<RoutePoint>? resolvedLocations = null) =>
            throw new HttpRequestException("provider unavailable");
    }

    private sealed class SuccessfulProvider(string name) : IRouteProvider
    {
        public string Name => name;

        public Task<ProviderRouteResult> GetRouteMetadataAsync(
            string source,
            string destination,
            List<string>? waypoints,
            CancellationToken cancellationToken = default,
            IReadOnlyList<RoutePoint>? resolvedLocations = null) =>
            Task.FromResult(new ProviderRouteResult
            {
                Provider = name,
                DistanceMeters = 69,
                ProviderRouteTimeSeconds = 6.7,
                Segments =
                [
                    new RouteSegment
                    {
                        Sequence = 0,
                        LengthMeters = 69,
                        Geometry = [new RoutePoint(10, 55), new RoutePoint(10.001, 55.001)]
                    }
                ]
            });
    }

    private sealed class StubGeocodingService : IGeocodingService
    {
        public Task<RoutePoint> GeocodeAsync(
            string geocodingUrl,
            string query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RoutePoint(10, 55));

        public IReadOnlyList<string> GenerateCandidateQueries(string rawQuery) => [rawQuery];
    }
}
