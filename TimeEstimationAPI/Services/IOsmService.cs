using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TimeEstimationAPI.Services;

public interface IRouteProvider
{
    string Name { get; }
    Task<OsmRouteMetadata> GetRouteMetadataAsync(string source, string destination, List<string>? waypoints, CancellationToken cancellationToken = default);
}

public interface IOsmService : IRouteProvider
{
}

/// <summary>Route values returned by an OSM-compatible provider.</summary>
public sealed class OsmRouteMetadata
{
    public string Provider { get; set; } = "OSRM";
    public double? DistanceMeters { get; set; }
    public double? DurationSeconds { get; set; }
    public string? RoadType { get; set; } = "primary";
    public double? SpeedLimitKmh { get; set; } = 50;
    public int? TrafficSignalCount { get; set; }
    public bool IsAvailable { get; set; } = true;
    public IReadOnlyCollection<string> MissingFields { get; set; } = [];
    public Dictionary<string, object> Raw { get; set; } = new();
}

/// <summary>Endpoints and switches for the geocoding and routing provider.</summary>
public sealed class OsmOptions
{
    public bool UseOsrm { get; set; }
    public string GeocodingUrl { get; set; } = "https://nominatim.openstreetmap.org/search";
    public string RoutingUrl { get; set; } = "https://router.project-osrm.org/route/v1/driving";
}

/// <summary>Uses Nominatim for place lookup and OSRM for route distance and time.</summary>
public sealed class OsrmService(HttpClient httpClient, IOptions<OsmOptions> options) : IOsmService
{
    private readonly OsmOptions _options = options.Value;
    public string Name => "OSRM";

    /// <summary>Geocodes the stops, asks OSRM for a route, and maps its response.</summary>
    public async Task<OsmRouteMetadata> GetRouteMetadataAsync(
        string source,
        string destination,
        List<string>? waypoints,
        CancellationToken cancellationToken = default)
    {
        var locations = new List<(double Longitude, double Latitude)>();
        foreach (var location in new[] { source }.Concat(waypoints ?? []).Concat(new[] { destination }))
        {
            locations.Add(await GeocodeAsync(location, cancellationToken));
        }

        var coordinates = string.Join(';', locations.Select(location => $"{location.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},{location.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        var url = $"{_options.RoutingUrl.TrimEnd('/')}/{coordinates}?overview=false";
        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

        var route = document.RootElement.GetProperty("routes").EnumerateArray().FirstOrDefault();
        if (route.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("The route provider returned no routes.");
        }

        return new OsmRouteMetadata
        {
            Provider = "OSRM",
            DistanceMeters = route.GetProperty("distance").GetDouble(),
            DurationSeconds = ParseDuration(route.GetProperty("duration")),
            RoadType = "primary",
            SpeedLimitKmh = 50,
            TrafficSignalCount = null,
            MissingFields = ["roadType", "speedLimitKmh", "trafficSignalCount"],
            Raw = new Dictionary<string, object> { ["provider"] = "OSRM" }
        };
    }

    private static double ParseDuration(JsonElement duration)
    {
        return duration.ValueKind == JsonValueKind.Number
            ? duration.GetDouble()
            : double.Parse(duration.GetString()!.TrimEnd('s'), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<(double Longitude, double Latitude)> GeocodeAsync(string query, CancellationToken cancellationToken)
    {
        var url = $"{_options.GeocodingUrl}?format=jsonv2&limit=1&q={Uri.EscapeDataString(query)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("TimeEstimationAPI/1.0");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var results = await response.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken: cancellationToken);
        var result = results?.FirstOrDefault() ?? throw new InvalidOperationException($"Location '{query}' could not be geocoded.");
        return (
            double.Parse(result.GetProperty("lon").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(result.GetProperty("lat").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>Small predictable provider used for local development and tests.</summary>
public sealed class OSMServiceStub : IOsmService
{
    public string Name => "development-stub";
    /// <summary>Returns stable sample data without making network calls.</summary>
    public Task<OsmRouteMetadata> GetRouteMetadataAsync(string source, string destination, List<string>? waypoints, CancellationToken cancellationToken = default)
    {
        var waypointCount = waypoints?.Count ?? 0;
        return Task.FromResult(new OsmRouteMetadata
        {
            Provider = "development-stub",
            DistanceMeters = 6500 + waypointCount * 1200,
            DurationSeconds = null,
            RoadType = "primary",
            SpeedLimitKmh = 50,
            TrafficSignalCount = null,
            MissingFields = ["durationSeconds", "trafficSignalCount"],
            Raw = new Dictionary<string, object> { ["provider"] = "development-stub" }
        });
    }
}
