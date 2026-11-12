using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TimeEstimationAPI.Models;

namespace TimeEstimationAPI.Services;

/// <summary>Endpoints and switches for the optional live OSRM provider.</summary>
public sealed class OsmOptions
{
    public bool UseOsrm { get; set; }
    public bool UseOverpass { get; set; }
    public string GeocodingUrl { get; set; } = "https://nominatim.openstreetmap.org/search";
    public string RoutingUrl { get; set; } = "https://router.project-osrm.org/route/v1/driving";
    public string OverpassUrl { get; set; } = "https://overpass.kumi.systems/api/interpreter";
    public string FallbackOverpassUrl { get; set; } = "https://overpass-api.de/api/interpreter";
    public string OverpassApiKey { get; set; } = string.Empty;
}

/// <summary>Geocodes stops, asks OSRM for route steps, then maps them to segments.</summary>
public sealed class OsrmService(
    HttpClient httpClient,
    IOptions<OsmOptions> options,
    IGeocodingService geocodingService) : IRouteProvider
{
    private readonly OsmOptions _options = options.Value;
    public string Name => "OSRM";

    public async Task<ProviderRouteResult> GetRouteMetadataAsync(
        string source,
        string destination,
        List<string>? waypoints,
        CancellationToken cancellationToken = default,
        IReadOnlyList<RoutePoint>? resolvedLocations = null)
    {
        var locations = resolvedLocations?.ToList() ?? [];
        if (resolvedLocations is null)
        {
            locations.Add(await GeocodeAsync(source, cancellationToken));
            foreach (var waypoint in waypoints ?? [])
            {
                try
                {
                    locations.Add(await GeocodeAsync(waypoint, cancellationToken));
                }
                catch (InvalidOperationException)
                {
                    throw new InvalidViaLocationException(waypoint);
                }
            }
            locations.Add(await GeocodeAsync(destination, cancellationToken));
        }

        var coordinates = string.Join(';', locations.Select(point =>
            $"{point.Longitude.ToString(CultureInfo.InvariantCulture)},{point.Latitude.ToString(CultureInfo.InvariantCulture)}"));
        var url = $"{_options.RoutingUrl.TrimEnd('/')}/{coordinates}?overview=full&steps=true&annotations=true&geometries=geojson";

        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var route = document.RootElement.GetProperty("routes").EnumerateArray().FirstOrDefault();
        if (route.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("The route provider returned no routes.");

        var segments = ParseSteps(route);
        return new ProviderRouteResult
        {
            Provider = Name,
            DistanceMeters = route.GetProperty("distance").GetDouble(),
            ProviderRouteTimeSeconds = ParseDuration(route.GetProperty("duration")),
            TrafficSignalCount = CountTrafficSignals(segments),
            MissingFields = ["roadType", "speedLimitKmh", "laneCount"],
            Segments = segments
        };
    }

    private static List<RouteSegment> ParseSteps(JsonElement route)
    {
        var segments = new List<RouteSegment>();
        var sequence = 0;
        foreach (var leg in route.GetProperty("legs").EnumerateArray())
        {
            foreach (var step in leg.GetProperty("steps").EnumerateArray())
            {
                var intersections = step.TryGetProperty("intersections", out var values)
                    ? values.EnumerateArray().ToArray()
                    : [];
                var hasSignal = intersections.Any(IsTrafficSignal);
                var laneCount = intersections
                    .SelectMany(GetLaneCount)
                    .DefaultIfEmpty()
                    .Max();
                var isRoundabout = step.TryGetProperty("maneuver", out var maneuver) &&
                    maneuver.TryGetProperty("type", out var type) &&
                    type.GetString()?.Contains("roundabout", StringComparison.OrdinalIgnoreCase) == true;

                segments.Add(new RouteSegment
                {
                    Sequence = sequence++,
                    LengthMeters = GetDouble(step, "distance"),
                    ProviderSegmentTimeSeconds = GetDouble(step, "duration"),
                    LaneCount = laneCount > 0 ? laneCount : null,
                    IsRoundabout = isRoundabout,
                    HasTrafficSignal = hasSignal,
                    IsIntersection = intersections.Length > 1,
                    Geometry = ParseStepGeometry(step),
                    MissingData = laneCount > 0
                        ? ["roadType", "speedLimitKmh", "nodeIds"]
                        : ["roadType", "speedLimitKmh", "laneCount", "nodeIds"]
                });
            }
        }

        return segments;
    }

    private static bool IsTrafficSignal(JsonElement intersection) =>
        intersection.TryGetProperty("traffic_signal", out var signal) &&
        signal.ValueKind == JsonValueKind.True;

    private static IEnumerable<int> GetLaneCount(JsonElement intersection)
    {
        if (intersection.TryGetProperty("lanes", out var lanes) &&
            lanes.ValueKind == JsonValueKind.Array)
            yield return lanes.GetArrayLength();
    }

    private static int CountTrafficSignals(IEnumerable<RouteSegment> segments) =>
        segments.Count(segment => segment.HasTrafficSignal == true);

    /// <summary>
    /// Parses step coordinates from OSRM JSON, falling back to intersection coordinates
    /// when explicit step geometry is unavailable.
    /// </summary>
    private static IReadOnlyList<RoutePoint> ParseStepGeometry(JsonElement step)
    {
        if (step.TryGetProperty("geometry", out var geometry) &&
            geometry.TryGetProperty("coordinates", out var coordinates))
        {
            var points = coordinates.EnumerateArray()
                .Where(point => point.GetArrayLength() >= 2)
                .Select(point => new RoutePoint(point[0].GetDouble(), point[1].GetDouble()))
                .ToArray();
            if (points.Length > 0)
                return points;
        }

        if (step.TryGetProperty("intersections", out var intersections))
        {
            return intersections.EnumerateArray()
                .Where(intersection => intersection.TryGetProperty("location", out var loc) && loc.GetArrayLength() >= 2)
                .Select(intersection =>
                {
                    var loc = intersection.GetProperty("location");
                    return new RoutePoint(loc[0].GetDouble(), loc[1].GetDouble());
                })
                .ToArray();
        }

        return [];
    }

    private static double? GetDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static double ParseDuration(JsonElement duration) =>
        duration.ValueKind == JsonValueKind.Number
            ? duration.GetDouble()
            : double.Parse(duration.GetString()!.TrimEnd('s'), CultureInfo.InvariantCulture);

    private Task<RoutePoint> GeocodeAsync(string query, CancellationToken cancellationToken) =>
        geocodingService.GeocodeAsync(_options.GeocodingUrl, query, cancellationToken);
}
