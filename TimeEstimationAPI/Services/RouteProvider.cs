using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TimeEstimationAPI.Models;

namespace TimeEstimationAPI.Services;

public interface IRouteProvider
{
    string Name { get; }

    Task<ProviderRouteResult> GetRouteMetadataAsync(
        string source,
        string destination,
        List<string>? waypoints,
        CancellationToken cancellationToken = default);
}

/// <summary>Common route data returned by a routing provider.</summary>
public sealed class ProviderRouteResult
{
    public string Provider { get; set; } = "OSRM";
    public double? DistanceMeters { get; set; }
    public double? ProviderEstimatedDurationSeconds { get; set; }
    public string? RoadType { get; set; }
    public double? SpeedLimitKmh { get; set; }
    public int? TrafficSignalCount { get; set; }
    public bool IsAvailable { get; set; } = true;
    public IReadOnlyCollection<string> MissingFields { get; set; } = [];
    public IReadOnlyList<RoutePoint> Geometry { get; set; } = [];
    public IReadOnlyList<RouteSegment> Segments { get; set; } = [];
}

/// <summary>Endpoints and switches for the optional live OSRM provider.</summary>
public sealed class OsmOptions
{
    public bool UseOsrm { get; set; }
    public string GeocodingUrl { get; set; } = "https://nominatim.openstreetmap.org/search";
    public string RoutingUrl { get; set; } = "https://router.project-osrm.org/route/v1/driving";
}

/// <summary>Geocode stops, asks OSRM for route steps, then maps them to segments.</summary>
public sealed class OsrmService(HttpClient httpClient, IOptions<OsmOptions> options) : IRouteProvider
{
    private readonly OsmOptions _options = options.Value;
    public string Name => "OSRM";

    public async Task<ProviderRouteResult> GetRouteMetadataAsync(
        string source,
        string destination,
        List<string>? waypoints,
        CancellationToken cancellationToken = default)
    {
        var locations = new List<RoutePoint>();
        foreach (var location in new[] { source }.Concat(waypoints ?? []).Concat(new[] { destination }))
            locations.Add(await GeocodeAsync(location, cancellationToken));

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
            ProviderEstimatedDurationSeconds = ParseDuration(route.GetProperty("duration")),
            TrafficSignalCount = CountTrafficSignals(segments),
            MissingFields = ["roadType", "speedLimitKmh", "laneCount"],
            Geometry = ParseGeometry(route),
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
                    ProviderEstimatedDurationSeconds = GetDouble(step, "duration"),
                    LaneCount = laneCount > 0 ? laneCount : null,
                    IsRoundabout = isRoundabout,
                    HasTrafficSignal = hasSignal,
                    IsIntersection = intersections.Length > 1,
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

    private static IReadOnlyList<RoutePoint> ParseGeometry(JsonElement route)
    {
        if (!route.TryGetProperty("geometry", out var geometry) ||
            !geometry.TryGetProperty("coordinates", out var coordinates))
            return [];

        return coordinates.EnumerateArray()
            .Where(point => point.GetArrayLength() >= 2)
            .Select(point => new RoutePoint(point[0].GetDouble(), point[1].GetDouble()))
            .ToArray();
    }

    private static double? GetDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static double ParseDuration(JsonElement duration) =>
        duration.ValueKind == JsonValueKind.Number
            ? duration.GetDouble()
            : double.Parse(duration.GetString()!.TrimEnd('s'), CultureInfo.InvariantCulture);

    private async Task<RoutePoint> GeocodeAsync(string query, CancellationToken cancellationToken)
    {
        var url = $"{_options.GeocodingUrl}?format=jsonv2&limit=1&q={Uri.EscapeDataString(query)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("TimeEstimationAPI/1.0");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var results = await response.Content.ReadFromJsonAsync<JsonElement[]>(
            cancellationToken: cancellationToken);
        var result = results?.FirstOrDefault()
            ?? throw new InvalidOperationException($"Location '{query}' could not be geocoded.");
        return new RoutePoint(
            double.Parse(result.GetProperty("lon").GetString()!, CultureInfo.InvariantCulture),
            double.Parse(result.GetProperty("lat").GetString()!, CultureInfo.InvariantCulture));
    }
}

/// <summary>Predictable offline provider used for local development and tests.</summary>
public sealed class DevelopmentRouteProvider : IRouteProvider
{
    public string Name => "development-stub";

    public Task<ProviderRouteResult> GetRouteMetadataAsync(
        string source,
        string destination,
        List<string>? waypoints,
        CancellationToken cancellationToken = default)
    {
        var segmentCount = Math.Max((waypoints?.Count ?? 0) + 1, 1);
        var segments = Enumerable.Range(0, segmentCount)
            .Select(index => new RouteSegment
            {
                Sequence = index,
                LengthMeters = 6500d / segmentCount,
                ProviderEstimatedDurationSeconds = null,
                RoadType = "primary",
                SpeedLimitKmh = 50,
                MissingData = ["providerEstimatedDurationSeconds"]
            })
            .ToArray();

        return Task.FromResult(new ProviderRouteResult
        {
            Provider = Name,
            DistanceMeters = 6500,
            RoadType = "primary",
            SpeedLimitKmh = 50,
            MissingFields = ["providerEstimatedDurationSeconds", "trafficSignalCount"],
            Segments = segments
        });
    }
}
