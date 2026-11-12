using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using TimeEstimationAPI.Models;

namespace TimeEstimationAPI.Services;

/// <summary>Best-effort OSM tag enrichment for provider-generated segments.</summary>
public sealed class OsmTagEnricher(
    HttpClient httpClient,
    IOptions<OsmOptions> options,
    ILogger<OsmTagEnricher> logger,
    IMemoryCache cache)
{
    // The query radius is deliberately wider than the final match tolerance.
    // It accommodates small OSRM/OSM geometry offsets without fetching an entire neighbourhood.
    private const double CorridorRadiusMeters = 50;
    // Sampling avoids producing a huge Overpass query for dense OSRM geometries.
    // Significant turns and OSRM step boundaries are retained even when they are closer.
    private const double PointSpacingMeters = 67;
    private const double TurnAngleThresholdDegrees = 30;
    private const int MaxQueryPoints = 100;
    private const int MaxQueryBatches = 10;
    private const int QueryTimeoutSeconds = 25;
    private const int RequestTimeoutSeconds = 20;
    private const int MaxAttemptsPerEndpoint = 2;
    // Repeated route requests commonly use the same roads during local development.
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);

    // Roughly 11 m at this latitude. This is actual road-match tolerance and remains much narrower than CorridorRadiusMeters to reject parallel roads.
    // Keep this value conservative because OSM and OSRM geometries can differ slightly. Heading and complete-geometry checks below provide additional road selection.
    private const double MaxMatchDistanceMeters = 12;
    private readonly OsmOptions _options = options.Value;

    public async Task<IReadOnlyList<RouteSegment>> EnrichAsync(
        IReadOnlyList<RouteSegment> segments,
        CancellationToken cancellationToken)
    {
        if (segments.Count == 0)
            return segments;

        if (!_options.UseOverpass)
        {
            logger.LogInformation("Overpass OSM tag enrichment is disabled. Preserving unavailable OSM fields.");
            return segments;
        }

        if (segments.All(segment => segment.Geometry.Count == 0))
            return segments;

        // Query a narrow corridor around the route rather than a full route bounding box.
        var routePoints = SimplifyRoutePoints(segments);
        var waysById = new Dictionary<long, OsmWay>();
        foreach (var pointBatch in routePoints.Chunk(MaxQueryPoints).Take(MaxQueryBatches))
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var query = BuildCorridorQuery(pointBatch);
            var batchWays = await FetchWaysAsync(query, cancellationToken);
            if (batchWays is not null)
            {
                // Adjacent corridor batches overlap, so deduplicate ways before matching.
                foreach (var way in batchWays)
                    waysById[way.Id] = way;
            }
        }

        if (waysById.Count == 0)
        {
            return segments;
        }

        var enriched = segments.Select(segment => EnrichSegment(segment, waysById.Values)).ToArray();
        return enriched;
    }

    private async Task<IReadOnlyList<OsmWay>?> FetchWaysAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var cacheKey = CreateCacheKey(query);
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<OsmWay>? cachedWays))
            return cachedWays;

        foreach (var endpoint in GetEndpoints())
        {
            for (var attempt = 1; attempt <= MaxAttemptsPerEndpoint; attempt++)
            {
                try
                {
                    using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    attemptCts.CancelAfter(TimeSpan.FromSeconds(RequestTimeoutSeconds));

                    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url)
                    {
                        Content = new FormUrlEncodedContent(
                            [new KeyValuePair<string, string>("data", query)])
                    };
                    // The managed service accepts the key in a header. Never send it to public fallbacks.
                    if (endpoint.RequiresApiKey)
                        request.Headers.Add("X-API-Key", _options.OverpassApiKey);

                    using var response = await httpClient.SendAsync(request, attemptCts.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        var retryable = IsRetryable(response.StatusCode);
                        logger.LogWarning(
                            "Overpass endpoint {Endpoint} returned HTTP {StatusCode} for corridor query.",
                            endpoint.Url,
                            (int)response.StatusCode);
                        if (!retryable || attempt == MaxAttemptsPerEndpoint)
                            break;

                        await DelayBeforeRetryAsync(response, attempt, cancellationToken);
                        continue;
                    }

                    using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(
                        cancellationToken: attemptCts.Token);
                    var ways = document?.RootElement.GetProperty("elements")
                        .EnumerateArray()
                        .Select(ParseWay)
                        .Where(way => way is not null)
                        .Select(way => way!)
                        .ToArray() ?? [];
                    cache.Set(cacheKey, ways, CacheDuration);
                    return ways;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("Overpass endpoint {Endpoint} timed out for corridor query.", endpoint.Url);
                    if (attempt < MaxAttemptsPerEndpoint)
                        await DelayBeforeRetryAsync(null, attempt, cancellationToken);
                }
                catch (HttpRequestException exception)
                {
                    logger.LogWarning(exception, "Overpass endpoint {Endpoint} request failed.", endpoint.Url);
                    if (attempt < MaxAttemptsPerEndpoint)
                        await DelayBeforeRetryAsync(null, attempt, cancellationToken);
                }
                catch (JsonException exception)
                {
                    logger.LogWarning(exception, "Overpass endpoint {Endpoint} returned invalid JSON.", endpoint.Url);
                    break;
                }
            }
        }

        return null;
    }

    private IEnumerable<OverpassEndpoint> GetEndpoints()
    {
        var endpoints = new List<OverpassEndpoint>();
        // Prefer the authenticated managed service when configured; public endpoints remain a fallback.
        if (!string.IsNullOrWhiteSpace(_options.OverpassApiKey))
            endpoints.Add(new("https://api.overpass.ch/v1/interpreter", true));

        AddEndpoint(endpoints, _options.OverpassUrl);
        AddEndpoint(endpoints, _options.FallbackOverpassUrl);
        AddEndpoint(endpoints, "https://z.overpass-api.de/api/interpreter");
        AddEndpoint(endpoints, "https://lz4.overpass-api.de/api/interpreter");
        AddEndpoint(endpoints, "https://overpass.kumi.systems/api/interpreter");
        AddEndpoint(endpoints, "https://overpass.osm.ch/api/interpreter");
        return endpoints;

        void AddEndpoint(ICollection<OverpassEndpoint> collection, string url)
        {
            if (!string.IsNullOrWhiteSpace(url) &&
                !collection.Any(endpoint => string.Equals(endpoint.Url, url, StringComparison.OrdinalIgnoreCase)))
                collection.Add(new(url, false));
        }
    }

    private async Task DelayBeforeRetryAsync(
        HttpResponseMessage? response,
        int attempt,
        CancellationToken cancellationToken)
    {
        // Honour server-provided retry timing, otherwise use a small bounded exponential delay.
        var delay = response?.Headers.RetryAfter?.Delta
            ?? TimeSpan.FromSeconds(Math.Min(8, attempt * 2));
        await Task.Delay(delay, cancellationToken);
    }

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            (HttpStatusCode)429 or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

    private static RouteSegment EnrichSegment(RouteSegment segment, IEnumerable<OsmWay> ways)
    {
        if (segment.Geometry.Count == 0)
            return segment;

        // Compare the complete OSRM step geometry, not only its midpoint. 
        // Reduces false matches on parallel urban roads and ramps. Heading breaks ties where roads are close.
        var nearest = ways
            .Where(way => way.Geometry.Count > 0)
            .Select(way => (
                Way: way,
                AverageDistance: AverageDistanceSquaredToWay(segment.Geometry, way.Geometry),
                MaximumDistance: MaximumDistanceSquaredToWay(segment.Geometry, way.Geometry),
                HeadingDifference: HeadingDifferenceDegrees(segment.Geometry, way.Geometry)))
            .Where(match => match.MaximumDistance <= MaxMatchDistanceMeters * MaxMatchDistanceMeters)
            .OrderBy(match => match.HeadingDifference > 45 ? 1 : 0)
            .ThenBy(match => match.HeadingDifference)
            .ThenBy(match => match.AverageDistance)
            .FirstOrDefault();
        if (nearest.Way is null)
            return segment;

        var tags = nearest.Way.Tags;
        // OSM's "highway" tag means "road type" in OSM terminology, not "highway" in the literal sense. 
        // MLM expects a road type string, like: "motorway", "primary", "secondary", "tertiary", "residential", "unclassified", "service". The model maps these to numeric codes.
        var roadType = tags.TryGetValue("highway", out var highway) ? highway : segment.RoadType;
        // OSM's "maxspeed" tag is a string, e.g. "50 mph" or "80". The model expects a numeric km/h value.
        var speedLimit = ParseSpeed(tags.TryGetValue("maxspeed", out var maxspeed) ? maxspeed : null)
            ?? segment.SpeedLimitKmh;
        // OSM's "lanes" tag is a string, e.g. "2" or "2;1". The model expects a numeric lane count.
        var laneCount = ParseLanes(tags.TryGetValue("lanes", out var lanes) ? lanes : null)
            ?? ParseLanes(tags.TryGetValue("lanes:forward", out var forwardLanes) ? forwardLanes : null)
            ?? segment.LaneCount;
        // Preserve a positive provider signal. A 2D OSM match cannot reliably distinguish an overpass from the road directly underneath.
        var bridge = segment.IsBridge == true
            ? true
            : tags.TryGetValue("bridge", out var bridgeValue)
                ? ParseBooleanTag(bridgeValue)
                : segment.IsBridge;
        var tunnel = segment.IsTunnel == true
            ? true
            : tags.TryGetValue("tunnel", out var tunnelValue)
                ? ParseBooleanTag(tunnelValue)
                : segment.IsTunnel;

        var missing = segment.MissingData
            .Where(field => (field, roadType, speedLimit, laneCount, bridge, tunnel) switch
            {
                ("roadType", not null, _, _, _, _) => false,
                ("speedLimitKmh", _, not null, _, _, _) => false,
                ("laneCount", _, _, not null, _, _) => false,
                ("bridge", _, _, _, not null, _) => false,
                ("tunnel", _, _, _, _, not null) => false,
                _ => true
            })
            .ToArray();

        return segment with
        {
            RoadType = roadType,
            OsmWayId = nearest.Way.Id,
            SpeedLimitKmh = speedLimit,
            LaneCount = laneCount,
            IsBridge = bridge,
            IsTunnel = tunnel,
            MissingData = missing
        };
    }

    private static OsmWay? ParseWay(JsonElement element)
    {
        if (!element.TryGetProperty("id", out var idElement) ||
            !element.TryGetProperty("geometry", out var geometryElement) ||
            !element.TryGetProperty("tags", out var tagsElement))
            return null;

        var geometry = geometryElement.EnumerateArray()
            .Where(point => point.TryGetProperty("lon", out _) && point.TryGetProperty("lat", out _))
            .Select(point => new RoutePoint(
                point.GetProperty("lon").GetDouble(),
                point.GetProperty("lat").GetDouble()))
            .ToArray();
        if (geometry.Length == 0)
            return null;

        var tags = tagsElement.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? "",
                StringComparer.OrdinalIgnoreCase);
        return new OsmWay(idElement.GetInt64(), geometry, tags);
    }

    private static double? ParseSpeed(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        // OSM values may contain units, ranges, or conditional text; use the first numeric value.
        var numeric = Regex.Match(value, @"\d+(?:[.,]\d+)?").Value.Replace(',', '.');
        return double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)
            ? (value.Contains("mph", StringComparison.OrdinalIgnoreCase) ? speed * 1.609344 : speed)
            : null;
    }

    private static int? ParseLanes(string? value) =>
        int.TryParse(
            Regex.Match(value ?? string.Empty, @"\d+").Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var lanes)
            ? lanes
            : null;

    private static bool ParseBooleanTag(string value) =>
        !value.Equals("no", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("false", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("0", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<RoutePoint> SimplifyRoutePoints(
        IReadOnlyList<RouteSegment> segments)
    {
        var routeSamples = new List<RoutePointSample>();
        foreach (var segment in segments)
        {
            for (var index = 0; index < segment.Geometry.Count; index++)
            {
                var sample = new RoutePointSample(
                    segment.Geometry[index],
                    index == 0 || index == segment.Geometry.Count - 1);
                if (routeSamples.Count > 0 &&
                    routeSamples[^1].Point.Equals(sample.Point))
                {
                    routeSamples[^1] = routeSamples[^1] with
                    {
                        Preserve = routeSamples[^1].Preserve || sample.Preserve
                    };
                }
                else
                {
                    routeSamples.Add(sample);
                }
            }
        }

        var simplified = new List<RoutePoint>();
        for (var index = 0; index < routeSamples.Count; index++)
        {
            var sample = routeSamples[index];
            var preserve = index == 0 ||
                index == routeSamples.Count - 1 ||
                sample.Preserve ||
                (simplified.Count > 0 &&
                    DistanceMeters(simplified[^1], sample.Point) >= PointSpacingMeters) ||
                IsSignificantTurn(routeSamples, index);
            if (preserve)
                simplified.Add(sample.Point);
        }

        return simplified;
    }

    private static bool IsSignificantTurn(
        IReadOnlyList<RoutePointSample> samples,
        int index)
    {
        if (index == 0 || index == samples.Count - 1)
            return false;

        var incoming = HeadingDegrees(samples[index - 1].Point, samples[index].Point);
        var outgoing = HeadingDegrees(samples[index].Point, samples[index + 1].Point);
        if (incoming is null || outgoing is null)
            return false;

        var difference = Math.Abs(incoming.Value - outgoing.Value);
        if (difference > 180)
            difference = 360 - difference;
        return difference >= TurnAngleThresholdDegrees;
    }

    private static string BuildCorridorQuery(IEnumerable<RoutePoint> points)
    {
        const string highwayFilter =
            "motorway|trunk|primary|secondary|tertiary|unclassified|residential|" +
            "living_street|service|motorway_link|trunk_link|primary_link|" +
            "secondary_link|tertiary_link";
        var clauses = string.Join(
            Environment.NewLine,
            points.Select(point =>
                $"  way[\"highway\"~\"^({highwayFilter})$\"]" +
                $"(around:{CorridorRadiusMeters.ToString(CultureInfo.InvariantCulture)}," +
                $"{point.Latitude.ToString(CultureInfo.InvariantCulture)}," +
                $"{point.Longitude.ToString(CultureInfo.InvariantCulture)});"));

        return $"""
            [out:json][timeout:{QueryTimeoutSeconds}];
            (
            {clauses}
            );
            out geom tags;
            """;
    }

    private static string CreateCacheKey(string query)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(query));
        return $"overpass:{Convert.ToHexString(hash)}";
    }

    // One degree of latitude is approximately 111.32 km. This local
    // equirectangular approximation is accurate enough for short road segments.
    private const double MetersPerDegree = 111_320;

    private static double DistanceMeters(RoutePoint first, RoutePoint second)
    {
        // Longitude degrees get shorter away from the equator, hence the cosine scale.
        var latitudeScale = Math.Cos(first.Latitude * Math.PI / 180);
        var deltaLongitude = (first.Longitude - second.Longitude) * MetersPerDegree * latitudeScale;
        var deltaLatitude = (first.Latitude - second.Latitude) * MetersPerDegree;
        return Math.Sqrt(deltaLongitude * deltaLongitude + deltaLatitude * deltaLatitude);
    }

    private static double HeadingDifferenceDegrees(
        IReadOnlyList<RoutePoint> routeGeometry,
        IReadOnlyList<RoutePoint> wayGeometry)
    {
        var routeHeading = GetGeometryHeading(routeGeometry);
        var wayHeading = GetGeometryHeading(wayGeometry);
        if (routeHeading is null || wayHeading is null)
            return 0;

        var difference = Math.Abs(routeHeading.Value - wayHeading.Value);
        if (difference > 180)
            difference = 360 - difference;
        // Road direction is not relevant for matching, so opposite directions are equivalent.
        return Math.Min(difference, 180 - difference);
    }

    private static double? GetGeometryHeading(IReadOnlyList<RoutePoint> geometry)
    {
        for (var index = 1; index < geometry.Count; index++)
        {
            var heading = HeadingDegrees(geometry[index - 1], geometry[index]);
            if (heading is not null)
                return heading;
        }

        return null;
    }

    private static double? HeadingDegrees(RoutePoint start, RoutePoint end)
    {
        if (DistanceMeters(start, end) < 1)
            return null;

        var latitudeScale = Math.Cos((start.Latitude + end.Latitude) / 2 * Math.PI / 180);
        var deltaLongitude = (end.Longitude - start.Longitude) * latitudeScale;
        var deltaLatitude = end.Latitude - start.Latitude;
        var heading = Math.Atan2(deltaLongitude, deltaLatitude) * 180 / Math.PI;
        return heading < 0 ? heading + 360 : heading;
    }

    private static double AverageDistanceSquaredToWay(
        IReadOnlyList<RoutePoint> routeGeometry,
        IReadOnlyList<RoutePoint> wayGeometry)
    {
        // Squared metres avoid a square root for every candidate comparison.
        return routeGeometry
            .Select(point => DistanceSquaredToWay(point, wayGeometry))
            .Average();
    }

    private static double DistanceSquaredToWay(
        RoutePoint point,
        IReadOnlyList<RoutePoint> geometry)
    {
        // Measure to the closest point on the OSM polyline, not just its vertices.
        var minimum = double.PositiveInfinity;
        for (var index = 0; index < geometry.Count; index++)
        {
            minimum = Math.Min(minimum, DistanceSquared(point, geometry[index]));
            if (index + 1 < geometry.Count)
                minimum = Math.Min(minimum, DistanceSquaredToLineSegment(
                    point, geometry[index], geometry[index + 1]));
        }

        return minimum;
    }

    private static double MaximumDistanceSquaredToWay(
        IReadOnlyList<RoutePoint> routeGeometry,
        IReadOnlyList<RoutePoint> wayGeometry) =>
        // Check both directions so a partially overlapping way cannot match.
        Math.Max(
            routeGeometry
                .Select(point => DistanceSquaredToWay(point, wayGeometry))
                .Max(),
            wayGeometry
                .Select(point => DistanceSquaredToWay(point, routeGeometry))
                .Max());

    private static double DistanceSquaredToLineSegment(
        RoutePoint point,
        RoutePoint start,
        RoutePoint end)
    {
        // Project onto the segment and clamp to its endpoints to find the nearest point.
        var deltaLongitude = end.Longitude - start.Longitude;
        var deltaLatitude = end.Latitude - start.Latitude;
        var lengthSquared = deltaLongitude * deltaLongitude + deltaLatitude * deltaLatitude;
        if (lengthSquared == 0)
            return DistanceSquared(point, start);

        var projection = ((point.Longitude - start.Longitude) * deltaLongitude +
            (point.Latitude - start.Latitude) * deltaLatitude) / lengthSquared;
        projection = Math.Clamp(projection, 0, 1);
        var nearest = new RoutePoint(
            start.Longitude + projection * deltaLongitude,
            start.Latitude + projection * deltaLatitude);
        return DistanceSquared(point, nearest);
    }

    private static double DistanceSquared(RoutePoint first, RoutePoint second)
    {
        // Return square metres: callers only compare distances, so sqrt is unnecessary.
        var latitudeScale = Math.Cos((first.Latitude + second.Latitude) / 2 * Math.PI / 180);
        var deltaLongitude = (first.Longitude - second.Longitude) * MetersPerDegree * latitudeScale;
        var deltaLatitude = (first.Latitude - second.Latitude) * MetersPerDegree;
        return deltaLongitude * deltaLongitude + deltaLatitude * deltaLatitude;
    }

    private sealed record OverpassEndpoint(string Url, bool RequiresApiKey);
    private sealed record RoutePointSample(RoutePoint Point, bool Preserve);
    private sealed record OsmWay(
        long Id,
        IReadOnlyList<RoutePoint> Geometry,
        IReadOnlyDictionary<string, string> Tags);
}
