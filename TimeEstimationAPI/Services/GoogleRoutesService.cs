using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TimeEstimationAPI.Services;

public interface IGoogleRoutesService
{
    Task<double?> GetDurationSecondsAsync(
        string source,
        string destination,
        IReadOnlyList<string> waypoints,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional settings for the Google Routes benchmark call.</summary>
public sealed class GoogleRoutesOptions
{
    public string? ApiKey { get; set; }
    public string Endpoint { get; set; } = "https://routes.googleapis.com/directions/v2:computeRoutes";
}

/// <summary>Calls Google Routes only when an API key has been configured.</summary>
public sealed class GoogleRoutesService(
    HttpClient httpClient,
    IOptions<GoogleRoutesOptions> options) : IGoogleRoutesService
{
    private readonly GoogleRoutesOptions _options = options.Value;

    /// <summary>Gets Google's duration for comparison; returns null without a key or route.</summary>
    public async Task<double?> GetDurationSecondsAsync(
        string source,
        string destination,
        IReadOnlyList<string> waypoints,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return null;
        }

        var payload = new
        {
            origin = new { address = source },
            destination = new { address = destination },
            intermediates = waypoints.Select(waypoint => new { address = waypoint }).ToArray(),
            travelMode = "DRIVE",
            routingPreference = "TRAFFIC_AWARE"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("X-Goog-Api-Key", _options.ApiKey);
        request.Headers.Add("X-Goog-FieldMask", "routes.duration,routes.distanceMeters");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var routes = document.RootElement.GetProperty("routes");
        var route = routes.EnumerateArray().FirstOrDefault();
        if (route.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return ParseDuration(route.GetProperty("duration"));
    }

    private static double ParseDuration(JsonElement duration)
    {
        return double.Parse(
            duration.GetString()!.TrimEnd('s'),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
