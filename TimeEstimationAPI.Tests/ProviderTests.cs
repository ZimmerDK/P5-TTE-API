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

    [Fact]
    public async Task OsrmService_includes_ordered_via_points_in_route_request()
    {
        HttpRequestMessage? capturedRequest = null;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return JsonResponse("""
            {
              "routes": [{
                "distance": 300, "duration": 60,
                "legs": [{"steps": []}, {"steps": []}]
              }]
            }
            """);
        }));
        var service = new OsrmService(
            httpClient,
            Options.Create(new OsmOptions { RoutingUrl = "https://osrm.test/route/v1/driving" }),
            new MappingGeocodingService(new Dictionary<string, RoutePoint>
            {
                ["source"] = new(10, 55),
                ["via-1"] = new(10.1, 55.1),
                ["via-2"] = new(10.2, 55.2),
                ["destination"] = new(10.3, 55.3)
            }));

        await service.GetRouteMetadataAsync(
            "source", "destination", ["via-1", "via-2"]);

        Assert.NotNull(capturedRequest);
        Assert.Contains(
            "/10,55;10.1,55.1;10.2,55.2;10.3,55.3?",
            capturedRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task GeocodingService_skips_empty_results_without_json_element_error()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            JsonResponse("[]")));
        var service = new GeocodingService(
            httpClient,
            NullLogger<GeocodingService>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GeocodeAsync(
                "https://nominatim.test/search",
                "unknown place"));

        Assert.Contains("unknown place", exception.Message);
    }

    [Fact]
    public async Task GeocodingService_reads_numeric_coordinates()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            JsonResponse("""[{"lon":10.1,"lat":55.2}]""")));
        var service = new GeocodingService(
            httpClient,
            NullLogger<GeocodingService>.Instance);

        var point = await service.GeocodeAsync(
            "https://nominatim.test/search",
            "numeric place");

        Assert.Equal(new RoutePoint(10.1, 55.2), point);
    }

    [Fact]
    public async Task OsrmService_reports_invalid_non_empty_via_location()
    {
        var service = new OsrmService(
            new HttpClient(new StubHttpMessageHandler(_ =>
                throw new InvalidOperationException("The route request should not be sent."))),
            Options.Create(new OsmOptions { RoutingUrl = "https://osrm.test/route/v1/driving" }),
            new FailingGeocodingService("bad via"));

        var exception = await Assert.ThrowsAsync<InvalidViaLocationException>(
            () => service.GetRouteMetadataAsync("source", "destination", ["bad via"]));

        Assert.Contains("bad via", exception.Message);
    }

    [Fact]
    public async Task GoogleRoutesService_sends_ordered_via_points_and_parses_route()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""
            {
              "routes": [{
                "distanceMeters": 1200,
                "duration": "90s",
                "staticDuration": "80s",
                "legs": [
                  {"distanceMeters": 500, "duration": "40s"},
                  {"distanceMeters": 700, "duration": "50s"}
                ]
              }]
            }
            """);
        }));
        var service = new GoogleRoutesService(
            httpClient,
            Options.Create(new GoogleMapsOptions
            {
                Enabled = true,
                ApiKey = "test-key"
            }),
            Options.Create(new OsmOptions
            {
                GeocodingUrl = "https://nominatim.test/search"
            }),
            new MappingGeocodingService(new Dictionary<string, RoutePoint>
            {
                ["source"] = new(10, 55),
                ["via"] = new(10.1, 55.1),
                ["destination"] = new(10.2, 55.2)
            }));

        var result = await service.GetRouteMetadataAsync("source", "destination", ["via"]);

        Assert.Equal(1200, result.DistanceMeters);
        Assert.Equal(90, result.ProviderRouteTimeSeconds);
        Assert.Equal(80, result.StaticRouteTimeSeconds);
        Assert.Equal(2, result.Segments.Count);
        Assert.NotNull(capturedRequest);
        Assert.Equal("test-key", capturedRequest!.Headers.GetValues("X-Goog-Api-Key").Single());
        var fieldMask = capturedRequest.Headers.GetValues("X-Goog-FieldMask").Single();
        Assert.Contains("routes.staticDuration", fieldMask);
        Assert.DoesNotContain("navigationInstruction", fieldMask);
        Assert.DoesNotContain("travelAdvisory", fieldMask);
        Assert.Contains("\"via\":true", capturedBody);
        Assert.True(capturedBody!.IndexOf("\"longitude\":10.1", StringComparison.Ordinal)
            > capturedBody.IndexOf("\"longitude\":10", StringComparison.Ordinal));
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

    private sealed class MappingGeocodingService(
        IReadOnlyDictionary<string, RoutePoint> points) : IGeocodingService
    {
        public Task<RoutePoint> GeocodeAsync(
            string geocodingUrl,
            string query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(points[query]);

        public IReadOnlyList<string> GenerateCandidateQueries(string rawQuery) => [rawQuery];
    }

    private sealed class FailingGeocodingService(string failingQuery) : IGeocodingService
    {
        public Task<RoutePoint> GeocodeAsync(
            string geocodingUrl,
            string query,
            CancellationToken cancellationToken = default) =>
            query == failingQuery
                ? throw new InvalidOperationException("Location could not be geocoded.")
                : Task.FromResult(new RoutePoint(10, 55));

        public IReadOnlyList<string> GenerateCandidateQueries(string rawQuery) => [rawQuery];
    }
}
