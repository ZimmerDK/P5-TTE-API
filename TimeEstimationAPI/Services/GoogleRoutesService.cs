using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TimeEstimationAPI.Models;

namespace TimeEstimationAPI.Services;

/// <summary>Configuration for the optional Google Routes API comparison provider.</summary>
public sealed class GoogleMapsOptions
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string RoutesUrl { get; set; } = "https://routes.googleapis.com/directions/v2:computeRoutes";
}

/// <summary>Gets an additional route duration and distance from Google Routes API.</summary>
public sealed class GoogleRoutesService(
    HttpClient httpClient,
    IOptions<GoogleMapsOptions> options,
    IOptions<OsmOptions> osmOptions,
    IGeocodingService geocodingService) : IRouteProvider
{
    private const string FieldMask =
        "routes.distanceMeters,routes.duration,routes.staticDuration," +
        "routes.legs.distanceMeters,routes.legs.duration";

    private readonly GoogleMapsOptions _options = options.Value;
    private readonly OsmOptions _osmOptions = osmOptions.Value;
    public string Name => "Google Routes";

    public async Task<ProviderRouteResult> GetRouteMetadataAsync(
        string source,
        string destination,
        List<string>? waypoints,
        CancellationToken cancellationToken = default,
        IReadOnlyList<RoutePoint>? resolvedLocations = null)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("Google Maps is enabled but GoogleMaps:ApiKey is not configured.");

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

        var payload = new
        {
            origin = ToWaypoint(locations[0]),
            destination = ToWaypoint(locations[^1]),
            intermediates = locations
                .Skip(1)
                .SkipLast(1)
                .Select(point => ToWaypoint(point, via: true))
                .ToArray(),
            travelMode = "DRIVE",
            routingPreference = "TRAFFIC_AWARE"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.RoutesUrl)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("X-Goog-Api-Key", _options.ApiKey);
        request.Headers.Add(
            "X-Goog-FieldMask",
            FieldMask);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Google Routes API returned {(int)response.StatusCode} ({response.ReasonPhrase}): {errorBody}");
        }

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var route = document.RootElement
            .GetProperty("routes")
            .EnumerateArray()
            .FirstOrDefault();
        if (route.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("Google Routes API returned no routes.");

        var segments = ParseLegs(route);
        return new ProviderRouteResult
        {
            Provider = Name,
            DistanceMeters = route.GetProperty("distanceMeters").GetDouble(),
            ProviderRouteTimeSeconds = ParseDuration(route.GetProperty("duration")),
            StaticRouteTimeSeconds = ParseDuration(route.GetProperty("staticDuration")),
            MissingFields = ["roadType", "speedLimitKmh", "laneCount"],
            Segments = segments
        };
    }

    private async Task<RoutePoint> GeocodeAsync(
        string query,
        CancellationToken cancellationToken) =>
        await geocodingService.GeocodeAsync(
            _osmOptions.GeocodingUrl,
            query,
            cancellationToken);

    private static object ToWaypoint(RoutePoint point, bool via = false) => new
    {
        via,
        location = new
        {
            latLng = new
            {
                latitude = point.Latitude,
                longitude = point.Longitude
            }
        }
    };

    private static List<RouteSegment> ParseLegs(JsonElement route)
    {
        var segments = new List<RouteSegment>();
        var sequence = 0;
        foreach (var leg in route.GetProperty("legs").EnumerateArray())
        {
            segments.Add(new RouteSegment
            {
                Sequence = sequence++,
                LengthMeters = leg.GetProperty("distanceMeters").GetDouble(),
                ProviderSegmentTimeSeconds = ParseDuration(leg.GetProperty("duration")),
                MissingData = ["roadType", "speedLimitKmh", "laneCount"]
            });
        }

        return segments;
    }

    private static double ParseDuration(JsonElement duration)
    {
        var value = duration.GetString()?.TrimEnd('s');
        return double.Parse(value ?? throw new JsonException("Google returned an invalid duration."), CultureInfo.InvariantCulture);
    }
}
