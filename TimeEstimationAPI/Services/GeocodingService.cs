using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TimeEstimationAPI.Models;

namespace TimeEstimationAPI.Services;

public interface IGeocodingService
{
    Task<RoutePoint> GeocodeAsync(
        string geocodingUrl,
        string query,
        CancellationToken cancellationToken = default);

    IReadOnlyList<string> GenerateCandidateQueries(string rawQuery);
}

public sealed partial class GeocodingService(
    HttpClient httpClient,
    ILogger<GeocodingService> logger) : IGeocodingService
{
    [GeneratedRegex(@"^(\d{4,5})\s+([A-Za-zÆØÅæøåÉéÈèÄäÖöÜü\s-]+?)(?:\s+[a-zA-Z]{1,3})?$")]
    private static partial Regex PostalCodeCityRegex();

    public async Task<RoutePoint> GeocodeAsync(
        string geocodingUrl,
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query cannot be empty.", nameof(query));

        var candidates = GenerateCandidateQueries(query);
        foreach (var candidate in candidates)
        {
            var point = await TryGeocodeSingleAsync(geocodingUrl, candidate, cancellationToken);
            if (point is not null)
            {
                if (!string.Equals(candidate, query.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(
                        "Geocoding location '{OriginalQuery}' failed directly; succeeded using fallback candidate '{FallbackQuery}'.",
                        query,
                        candidate);
                }
                return point;
            }
        }

        throw new InvalidOperationException($"Location '{query}' could not be geocoded.");
    }

    public IReadOnlyList<string> GenerateCandidateQueries(string rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
            return [];

        var trimmed = rawQuery.Trim();
        var candidates = new List<string> { trimmed };

        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length <= 1)
            return candidates;

        // Clean last part (e.g., postal code + city + potential trailing noise)
        var cleanedParts = (string[])parts.Clone();
        var lastPart = parts[^1];
        var match = PostalCodeCityRegex().Match(lastPart);
        if (match.Success)
        {
            cleanedParts[^1] = $"{match.Groups[1].Value} {match.Groups[2].Value.Trim()}";
        }

        var cleanedFull = string.Join(", ", cleanedParts);
        candidates.Add(cleanedFull);

        // Candidate: Drop POI/venue name (omit 1st part) -> Street + City
        if (cleanedParts.Length >= 2)
        {
            var streetAndCity = string.Join(", ", cleanedParts.Skip(1));
            candidates.Add(streetAndCity);
        }

        // Candidate: Keep POI/venue name + Postal/City (omit middle street parts)
        if (cleanedParts.Length >= 3)
        {
            var poiAndCity = $"{cleanedParts[0]}, {cleanedParts[^1]}";
            candidates.Add(poiAndCity);
        }

        // Candidate: Street name only (strip house number/letter if present) + Postal/City
        if (cleanedParts.Length >= 2)
        {
            var streetPart = cleanedParts[cleanedParts.Length - 2];
            var streetWithoutNumber = Regex.Replace(streetPart, @"\s+\d+.*$", "").Trim();
            if (!string.IsNullOrWhiteSpace(streetWithoutNumber) && !string.Equals(streetWithoutNumber, streetPart, StringComparison.OrdinalIgnoreCase))
            {
                var streetNameAndCity = $"{streetWithoutNumber}, {cleanedParts[^1]}";
                candidates.Add(streetNameAndCity);
            }
        }

        // Deduplicate candidates preserving insertion order
        var result = new List<string>();
        foreach (var c in candidates)
        {
            if (!result.Exists(existing => string.Equals(existing, c, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(c);
            }
        }

        return result;
    }

    private async Task<RoutePoint?> TryGeocodeSingleAsync(
        string geocodingUrl,
        string queryCandidate,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{geocodingUrl}?format=jsonv2&limit=1&q={Uri.EscapeDataString(queryCandidate)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("TimeEstimationAPI/1.0");

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var results = await response.Content.ReadFromJsonAsync<JsonElement[]>(cancellationToken: cancellationToken);
            var first = results?.FirstOrDefault();
            if (first.HasValue &&
                first.Value.TryGetProperty("lon", out var lonProp) &&
                first.Value.TryGetProperty("lat", out var latProp))
            {
                if (double.TryParse(lonProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) &&
                    double.TryParse(latProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
                {
                    return new RoutePoint(lon, lat);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Geocode lookup failed for candidate '{Candidate}'.", queryCandidate);
        }

        return null;
    }
}
