using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimeEstimationAPI.Models;
using TimeEstimationAPI.Services;
using Xunit;

public sealed class ProviderTests
{
    // Makes sure we can turn a normal OSRM response into the route data we use.
    [Fact]
    public async Task OsrmService_parses_steps_and_intersections()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            JsonResponse("""
            {
              "routes": [{
                "distance": 150.5, "duration": 30,
                "legs": [{
                  "steps": [{
                    "distance": 100, "duration": 20,
                    "geometry": {"coordinates": [[10.0, 55.0], [10.001, 55.001]]},
                    "intersections": [{"location": [10.0, 55.0], "lanes": [1, 2], "traffic_signal": true}],
                    "maneuver": {"type": "depart"}
                  }, {
                    "distance": 50.5, "duration": 10,
                    "geometry": {"coordinates": [[10.001, 55.001], [10.002, 55.002]]},
                    "intersections": [{"location": [10.001, 55.001]}],
                    "maneuver": {"type": "roundabout"}
                  }]
                }]
              }]
            }
            """)));
        var service = new OsrmService(
            httpClient,
            Options.Create(new OsmOptions { RoutingUrl = "https://osrm.test/route/v1/driving" }),
            new StubGeocodingService());

        var result = await service.GetRouteMetadataAsync("A", "B", null);

        Assert.Equal(150.5, result.DistanceMeters);
        Assert.Equal(30, result.ProviderRouteTimeSeconds);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(2, result.Segments[0].LaneCount);
        Assert.True(result.Segments[0].HasTrafficSignal);
        Assert.True(result.Segments[1].IsRoundabout);
        Assert.Equal(2, result.Segments[0].Geometry.Count);
    }

    // Checks that useful road details from Overpass get applied to the right segment.
    [Fact]
    public async Task OsmTagEnricher_applies_matching_way_tags()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            JsonResponse("""
            {
              "elements": [{
                "type": "way",
                "id": 12345,
                "geometry": [{"lon": 10.0, "lat": 55.0}, {"lon": 10.001, "lat": 55.001}],
                "tags": {
                  "highway": "primary", "maxspeed": "80",
                  "lanes": "2", "bridge": "yes"
                }
              }]
            }
            """)));
        var service = new OsmTagEnricher(
            httpClient,
            Options.Create(new OsmOptions
            {
                UseOverpass = true,
                OverpassUrl = "https://overpass.test/api/interpreter",
                FallbackOverpassUrl = string.Empty
            }),
            NullLogger<OsmTagEnricher>.Instance);

        var result = await service.EnrichAsync(
        [
            new RouteSegment
            {
                Sequence = 0,
                Geometry =
                [
                    new RoutePoint(10.0, 55.0),
                    new RoutePoint(10.001, 55.001)
                ],
                LengthMeters = 100,
                MissingData = ["roadType", "speedLimitKmh", "laneCount"]
            }
        ], CancellationToken.None);

        var segment = Assert.Single(result);
        Assert.Equal(12345, segment.OsmWayId);
        Assert.Equal("primary", segment.RoadType);
        Assert.Equal(80, segment.SpeedLimitKmh);
        Assert.Equal(2, segment.LaneCount);
        Assert.True(segment.IsBridge);
    }

    // Long provider segments should get split up, while their total time and length stay the same.
    [Fact]
    public void RouteDataCollector_splits_long_segments_preserving_duration()
    {
        var result = RouteDataCollector.SplitSegments(
        [
            new RouteSegment
            {
                Sequence = 7,
                LengthMeters = 450,
                ProviderSegmentTimeSeconds = 30,
                RoadType = "primary"
            }
        ], 200);

        Assert.Equal(3, result.Count);
        Assert.Equal([0, 1, 2], result.Select(segment => segment.Sequence));
        Assert.All(result, segment => Assert.Equal(150, segment.LengthMeters));
        Assert.All(result, segment => Assert.Equal(10, segment.ProviderSegmentTimeSeconds));
        Assert.All(result, segment => Assert.Equal("primary", segment.RoadType));
    }

    private static HttpResponseMessage JsonResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(
                System.Text.Json.JsonSerializer.Deserialize<object>(content)!)
        };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    private sealed class StubGeocodingService : IGeocodingService
    {
        public Task<RoutePoint> GeocodeAsync(
            string geocodingUrl,
            string query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(query == "A"
                ? new RoutePoint(10, 55)
                : new RoutePoint(10.002, 55.002));

        public IReadOnlyList<string> GenerateCandidateQueries(string rawQuery) => [rawQuery];
    }
}
