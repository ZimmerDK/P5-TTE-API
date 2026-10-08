using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TimeEstimationAPI.Services;

/// <summary>Settings for a local or hosted Valhalla instance.</summary>
public sealed class ValhallaOptions
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "http://localhost:8002/route";
    public string GeocodingUrl { get; set; } = "https://nominatim.openstreetmap.org/search";
}

/// <summary>Gets route distance and duration from Valhalla.</summary>
public sealed class ValhallaService(
    HttpClient httpClient,
    IOptions<ValhallaOptions> options) : IRouteProvider
{
    private readonly ValhallaOptions _options = options.Value;
    public string Name => "Valhalla";

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

        var payload = new
        {
            locations = locations.Select(location => new
            {
                lat = location.Latitude,
                lon = location.Longitude,
                type = "break"
            }),
            costing = "auto",
            units = "kilometers"
        };

        using var response = await httpClient.PostAsJsonAsync(_options.Endpoint, payload, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var summary = document.RootElement.GetProperty("trip").GetProperty("summary");
        return new OsmRouteMetadata
        {
            Provider = Name,
            DistanceMeters = summary.GetProperty("length").GetDouble() * 1000,
            DurationSeconds = summary.GetProperty("time").GetDouble(),
            RoadType = null,
            SpeedLimitKmh = null,
            TrafficSignalCount = null,
            MissingFields = ["roadType", "speedLimitKmh", "trafficSignalCount"],
            Raw = new Dictionary<string, object> { ["provider"] = Name }
        };
    }

    private async Task<(double Longitude, double Latitude)> GeocodeAsync(
        string query,
        CancellationToken cancellationToken)
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
        return (
            double.Parse(result.GetProperty("lon").GetString()!, CultureInfo.InvariantCulture),
            double.Parse(result.GetProperty("lat").GetString()!, CultureInfo.InvariantCulture));
    }
}
