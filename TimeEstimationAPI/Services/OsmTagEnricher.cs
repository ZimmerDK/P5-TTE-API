using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TimeEstimationAPI.Models;

namespace TimeEstimationAPI.Services;

/// <summary>Best-effort OSM tag enrichment for provider-generated segments.</summary>
public sealed class OsmTagEnricher(
    HttpClient httpClient,
    IOptions<OsmOptions> options,
    ILogger<OsmTagEnricher> logger)
{
    // 0.22 degrees is roughly 24 km north/south. Keeping each Overpass box below this size avoids very large queries.
    private const double MaxQuerySpanDegrees = 0.22;

    // 0.0001 degrees is roughly 11 m north/south. 
    // Every OSRM geometry point must be this close to the candidate OSM way; 
    // using a small threshold is safer in dense urban areas with lots of parallel roads and such.
    // Change this value and the corresponding README explanation together.
    private const double MaxMatchDistanceDegrees = 0.0001;
    private readonly OsmOptions _options = options.Value;

    public async Task<IReadOnlyList<RouteSegment>> EnrichAsync(
        IReadOnlyList<RouteSegment> segments,
        CancellationToken cancellationToken)
    {
        if (segments.Count == 0)
            return segments;

        if (!_options.UseOverpass)
        {
            logger.LogInformation("Overpass OSM tag enrichment is disabled. Applying fallback tags.");
            return ApplyFallbackTags(segments);
        }

        var points = segments.SelectMany(segment => segment.Geometry).ToArray();
        if (points.Length == 0)
            return ApplyFallbackTags(segments);

        var south = points.Min(point => point.Latitude);
        var north = points.Max(point => point.Latitude);
        var west = points.Min(point => point.Longitude);
        var east = points.Max(point => point.Longitude);
        var ways = new List<OsmWay>();
        // Cap bounding box queries to a maximum of 15 to avoid rate-limiting/timeouts on long routes while providing sufficient spatial coverage.
        foreach (var box in CreateBoundingBoxes(south, west, north, east).Take(15))
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var boxWays = await FetchWaysAsync(box, cancellationToken);
            if (boxWays is not null)
            {
                ways.AddRange(boxWays);
            }
            else
            {
                // Stop attempting remaining bounding boxes if Overpass endpoints failed or timed out.
                break;
            }
        }

        if (ways.Count == 0)
        {
            return ApplyFallbackTags(segments);
        }

        var enriched = segments.Select(segment => EnrichSegment(segment, ways)).ToArray();
        return ApplyFallbackTags(enriched);
    }

    /// <summary>
    /// Evaluates route segments against missing attributes and applies dynamic fallback logic.<br/>
    /// 1. Speed limit: If missing, calculates implied travel speed from OSRM distance and duration ((length / time) * 3.6),
    ///    then snaps to standard legal speed limits (30, 40, 50, 70, 80, 90, 110, 130 km/h).<br/>
    /// 2. Road type: If missing, infers classification from speed limit (>= 90 motorway, >= 70 primary, else secondary).<br/>
    /// 3. Lane count: Defaults to 2 if unpopulated.
    /// </summary>
    private static IReadOnlyList<RouteSegment> ApplyFallbackTags(IReadOnlyList<RouteSegment> segments)
    {
        return segments.Select(segment =>
        {
            // 1. Calculate or preserve speed limit
            var speedLimit = (segment.SpeedLimitKmh.HasValue && segment.SpeedLimitKmh.Value > 0)
                ? segment.SpeedLimitKmh.Value
                : (segment.LengthMeters > 0 && segment.ProviderSegmentTimeSeconds > 0)
                    // Compute implied speed (m/s -> km/h) from OSRM step length/duration, then snap to legal speed limit
                    ? SnapToStandardSpeedLimit((segment.LengthMeters.Value / segment.ProviderSegmentTimeSeconds.Value) * 3.6, segment.RoadType)
                    : 50.0; // Fall back to baseline urban speed if metrics are unavailable

            // 2. Infer or preserve road type
            var roadType = !string.IsNullOrWhiteSpace(segment.RoadType)
                ? segment.RoadType
                : speedLimit switch
                {
                    >= 90 => "motorway",
                    >= 70 => "primary",
                    _ => "secondary"
                };

            // 3. Set or preserve lane count
            var laneCount = (segment.LaneCount.HasValue && segment.LaneCount.Value > 0)
                ? segment.LaneCount.Value
                : 2;

            // Remove populated fields from missingData array
            var missing = segment.MissingData
                .Where(field => field switch
                {
                    "roadType" => false,
                    "speedLimitKmh" => false,
                    "laneCount" => false,
                    _ => true
                })
                .ToArray();

            return segment with
            {
                RoadType = roadType,
                SpeedLimitKmh = speedLimit,
                LaneCount = laneCount,
                MissingData = missing
            };
        }).ToArray();
    }

    private static double SnapToStandardSpeedLimit(double calculatedSpeedKmh, string? roadType)
    {
        static double Snap(double speed) => speed switch
        {
            <= 35 => 30.0,
            <= 45 => 40.0,
            <= 60 => 50.0,
            <= 75 => 70.0,
            <= 85 => 80.0,
            <= 95 => 90.0,
            <= 120 => 110.0,
            _ => 130.0
        };

        var baseLimit = roadType switch
        {
            "motorway" or "motorway_link" => 110.0,
            "primary" or "primary_link" or "trunk" or "trunk_link" => 80.0,
            "secondary" or "secondary_link" or "tertiary" or "tertiary_link" => 50.0,
            "residential" or "living_street" => 30.0,
            _ => 50.0
        };

        var snapped = Snap(calculatedSpeedKmh);
        return Math.Max(snapped, baseLimit);
    }

    private async Task<IReadOnlyList<OsmWay>?> FetchWaysAsync(
        BoundingBox box,
        CancellationToken cancellationToken)
    {
        var query = $"""
            [out:json][timeout:15];
            way["highway"~"^(motorway|trunk|primary|secondary|tertiary|unclassified|residential|motorway_link|trunk_link|primary_link|secondary_link|tertiary_link)$"](
              {box.South.ToString(CultureInfo.InvariantCulture)},
              {box.West.ToString(CultureInfo.InvariantCulture)},
              {box.North.ToString(CultureInfo.InvariantCulture)},
              {box.East.ToString(CultureInfo.InvariantCulture)}
            );
            out geom tags;
            """;

        var endpoints = new[]
        {
            _options.OverpassUrl,
            _options.FallbackOverpassUrl,
            "https://overpass.kumi.systems/api/interpreter",
            "https://overpass-api.de/api/interpreter"
        }
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct();

        foreach (var endpoint in endpoints)
        {
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(4));

                using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("data", query)]);
                using var response = await httpClient.PostAsync(endpoint, content, attemptCts.Token);
                response.EnsureSuccessStatusCode();
                using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(
                    cancellationToken: attemptCts.Token);
                return document?.RootElement.GetProperty("elements")
                    .EnumerateArray()
                    .Select(ParseWay)
                    .Where(way => way is not null)
                    .Select(way => way!)
                    .ToArray() ?? [];
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "OSM tag enrichment failed for query area using endpoint {Endpoint}.", endpoint);
            }
        }

        return null;
    }

    private static RouteSegment EnrichSegment(RouteSegment segment, IReadOnlyList<OsmWay> ways)
    {
        if (segment.Geometry.Count == 0)
            return segment;

        // Compare the complete OSRM step geometry, not only its midpoint. Reduces false matches on parallel urban roads and ramps etc.
        var nearest = ways
            .Select(way => (
                Way: way,
                AverageDistance: AverageDistanceSquaredToWay(segment.Geometry, way.Geometry),
                MaximumDistance: MaximumDistanceSquaredToWay(segment.Geometry, way.Geometry)))
            .Where(match => match.MaximumDistance <= MaxMatchDistanceDegrees * MaxMatchDistanceDegrees)
            .OrderBy(match => match.AverageDistance)
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
            ?? segment.LaneCount;
        // Preserve a positive provider signal. A 2D OSM match cannot reliably distinguish an overpass from the road directly underneath.
        var bridge = segment.IsBridge == true
            ? true
            : tags.TryGetValue("bridge", out var bridgeValue)
                ? !string.Equals(bridgeValue, "no", StringComparison.OrdinalIgnoreCase)
                : segment.IsBridge;
        var tunnel = segment.IsTunnel == true
            ? true
            : tags.TryGetValue("tunnel", out var tunnelValue)
                ? !string.Equals(tunnelValue, "no", StringComparison.OrdinalIgnoreCase)
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
        var numeric = new string(value.TakeWhile(character => char.IsDigit(character) || character == '.').ToArray());
        return double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)
            ? (value.Contains("mph", StringComparison.OrdinalIgnoreCase) ? speed * 1.609344 : speed)
            : null;
    }

    private static int? ParseLanes(string? value) =>
        int.TryParse(value?.Split(';', StringSplitOptions.TrimEntries).FirstOrDefault(),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var lanes) ? lanes : null;

    private static IEnumerable<BoundingBox> CreateBoundingBoxes(
        double south,
        double west,
        double north,
        double east)
    {
        for (var boxSouth = south; boxSouth < north || boxSouth == south; boxSouth += MaxQuerySpanDegrees)
        {
            var boxNorth = Math.Min(boxSouth + MaxQuerySpanDegrees, north);
            for (var boxWest = west; boxWest < east || boxWest == west; boxWest += MaxQuerySpanDegrees)
            {
                var boxEast = Math.Min(boxWest + MaxQuerySpanDegrees, east);
                yield return new BoundingBox(boxSouth, boxWest, boxNorth, boxEast);
                if (boxEast >= east)
                    break;
            }

            if (boxNorth >= north)
                break;
        }
    }

    private static double AverageDistanceSquaredToWay(
        IReadOnlyList<RoutePoint> routeGeometry,
        IReadOnlyList<RoutePoint> wayGeometry)
    {
        return routeGeometry
            .Select(point => DistanceSquaredToWay(point, wayGeometry))
            .Average();
    }

    private static double DistanceSquaredToWay(
        RoutePoint point,
        IReadOnlyList<RoutePoint> geometry)
    {
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
        routeGeometry
            .Select(point => DistanceSquaredToWay(point, wayGeometry))
            .Max();

    private static double DistanceSquaredToLineSegment(
        RoutePoint point,
        RoutePoint start,
        RoutePoint end)
    {
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

    private static double DistanceSquared(RoutePoint first, RoutePoint second) =>
        (first.Longitude - second.Longitude) * (first.Longitude - second.Longitude) +
        (first.Latitude - second.Latitude) * (first.Latitude - second.Latitude);

    private sealed record BoundingBox(double South, double West, double North, double East);
    private sealed record OsmWay(
        long Id,
        IReadOnlyList<RoutePoint> Geometry,
        IReadOnlyDictionary<string, string> Tags);
}
