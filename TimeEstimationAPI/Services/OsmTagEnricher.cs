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

        var points = segments.SelectMany(segment => segment.Geometry).ToArray();
        if (points.Length == 0)
            return segments;

        var south = points.Min(point => point.Latitude);
        var north = points.Max(point => point.Latitude);
        var west = points.Min(point => point.Longitude);
        var east = points.Max(point => point.Longitude);
        var ways = new List<OsmWay>();
        foreach (var box in CreateBoundingBoxes(south, west, north, east))
        {
            var boxWays = await FetchWaysAsync(box, cancellationToken);
            if (boxWays is not null)
                ways.AddRange(boxWays);
        }

        if (ways.Count == 0)
        {
            return segments;
        }

        return segments.Select(segment => EnrichSegment(segment, ways)).ToArray();
    }

    private async Task<IReadOnlyList<OsmWay>?> FetchWaysAsync(
        BoundingBox box,
        CancellationToken cancellationToken)
    {
        var query = $"""
            [out:json][timeout:20];
            way[highway](
              {box.South.ToString(CultureInfo.InvariantCulture)},
              {box.West.ToString(CultureInfo.InvariantCulture)},
              {box.North.ToString(CultureInfo.InvariantCulture)},
              {box.East.ToString(CultureInfo.InvariantCulture)}
            );
            out geom tags;
            """;
        var url = $"{_options.OverpassUrl}?data={Uri.EscapeDataString(query)}";

        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(
                cancellationToken: cancellationToken);
            return document?.RootElement.GetProperty("elements")
                .EnumerateArray()
                .Select(ParseWay)
                .Where(way => way is not null)
                .Select(way => way!)
                .ToArray() ?? [];
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "OSM tag enrichment was unavailable for one query area.");
            return null;
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "OSM tag enrichment returned invalid data for one query area.");
            return null;
        }
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
