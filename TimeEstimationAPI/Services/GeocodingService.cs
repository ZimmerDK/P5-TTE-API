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

public sealed class GeocodingService(
    HttpClient httpClient,
    ILogger<GeocodingService> logger) : IGeocodingService
{
    private const string UnitPattern = @"\d+\.\s*(tv|th|mf|sal|fl|floor)\.?|(st|kld)\.\s*(tv|th|mf)\.?|(apt|apartment|suite|ste|unit|bldg|building|fl|floor|rm|room|opgang|whg|app|box|door|sal|kld|st)\.?\s*#?[\w-]+|\d+(st|nd|rd|th)\s+floor|#[\w-]+";

    private static readonly Regex UnitSpecifierRegex = new($"^(?i)({UnitPattern})$", RegexOptions.Compiled);
    private static readonly Regex EmbeddedUnitRegex = new($@"(?i)\b({UnitPattern})\b", RegexOptions.Compiled);
    private static readonly Regex MultipleSpacesRegex = new(@"\s+", RegexOptions.Compiled);

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

    /// <summary>
    /// Generates language-agnostic query candidates by stripping sub-building details (floor/unit/apartment specifiers),
    /// merging inline unit details, or dropping redundant venue/country fields.
    /// Strictly preserves street/POI location anchors so geocoding never falls back to city, zip code, or country centroids.
    /// </summary>
    public IReadOnlyList<string> GenerateCandidateQueries(string rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
            return [];

        var trimmed = rawQuery.Trim();
        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length <= 1)
        {
            var cleanedSingle = CleanEmbeddedUnits(trimmed);
            return string.Equals(cleanedSingle, trimmed, StringComparison.OrdinalIgnoreCase)
                ? [trimmed]
                : [trimmed, cleanedSingle];
        }

        // 1. Original full query
        var candidates = new List<string> { trimmed };

        var nonUnitParts = new List<string>();
        var inlineUnitParts = new List<string>();
        var hasUnitParts = false;

        foreach (var p in parts)
        {
            if (UnitSpecifierRegex.IsMatch(p))
            {
                hasUnitParts = true;
                if (inlineUnitParts.Count > 0)
                    inlineUnitParts[^1] += $" {p}";
            }
            else
            {
                nonUnitParts.Add(p);
                inlineUnitParts.Add(p);
            }
        }

        // 2. Query with inline unit specifier (e.g. "Thulevej 44 4.tv, 9210 Aalborg SØ, Denmark")
        if (hasUnitParts && inlineUnitParts.Count > 0) candidates.Add(string.Join(", ", inlineUnitParts));
        if (hasUnitParts && nonUnitParts.Count > 0) candidates.Add(string.Join(", ", nonUnitParts));

        // 3. Queries without trailing country/region component (if >= 2 parts)
        if (parts.Length >= 2)
        {
            candidates.Add(string.Join(", ", parts.SkipLast(1)));
            if (hasUnitParts && nonUnitParts.Count >= 2) candidates.Add(string.Join(", ", nonUnitParts.SkipLast(1)));
            if (hasUnitParts && inlineUnitParts.Count >= 2) candidates.Add(string.Join(", ", inlineUnitParts.SkipLast(1)));
        }

        // 5. Clean embedded unit details inside individual parts
        var embeddedCleanedParts = parts.Select(CleanEmbeddedUnits).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (embeddedCleanedParts.Count > 0)
        {
            candidates.Add(string.Join(", ", embeddedCleanedParts));
            if (parts.Length >= 2 && embeddedCleanedParts.Count >= 2)
            {
                var cleanedNoCountry = string.Join(", ", embeddedCleanedParts.SkipLast(1));
                if (!string.IsNullOrWhiteSpace(cleanedNoCountry))
                    candidates.Add(cleanedNoCountry);
            }
        }

        // 6. Drop leading part (e.g. drop POI/venue name if multiple parts present) (e.g. "Salling Aarhus, Søndergade 27, 8000 Aarhus" -> "Søndergade 27, 8000 Aarhus")
        if (parts.Length >= 3)
        {
            candidates.Add(string.Join(", ", parts.Skip(1)));
            if (nonUnitParts.Count >= 3) candidates.Add(string.Join(", ", nonUnitParts.Skip(1)));
            // 7. Try POI name + city/zip (first part + last part before country) (e.g. "Salling Aarhus, 8000 Aarhus")
            var lastLocality = parts.Length >= 4 ? parts[^2] : parts[^1];
            candidates.Add($"{parts[0]}, {lastLocality}");
        }

        var requiredAnchors = GetRequiredLocationAnchors(parts, nonUnitParts);
        var countryPart = parts[^1].Trim();

        return candidates
            .Select(c => c.Trim())
            .Where(c => !string.IsNullOrWhiteSpace(c) &&
                        // Reject single country-only fallback when original query had street/city detail
                        !(parts.Length >= 2 && string.Equals(c, countryPart, StringComparison.OrdinalIgnoreCase)) &&
                        // Reject candidates that drop street/POI location anchors
                        (requiredAnchors.Count == 0 || requiredAnchors.Any(anchor => Regex.IsMatch(c, $@"\b{Regex.Escape(anchor)}\b", RegexOptions.IgnoreCase))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> GetRequiredLocationAnchors(string[] parts, List<string> nonUnitParts)
    {
        var requiredAnchors = new List<string>();
        if (parts.Length < 2 || nonUnitParts.Count == 0)
            return requiredAnchors;

        // Primary anchor: first non-unit part
        AddAnchorTerms(requiredAnchors, nonUnitParts[0]); 

        // Secondary anchor: second non-unit part if present and first is a pure venue name while second is a street address
        if (nonUnitParts.Count >= 3)
        {
            var first = nonUnitParts[0];
            var second = nonUnitParts[1];
            const string streetPattern = @"(?i)\b(street|gade|vej|straße|str|via|piazza|road|rd|avenue|ave|blvd|drive|dr|lane|ln|way|place|pl|square|sq)\b";
            var secondLooksLikeStreet = Regex.IsMatch(second, streetPattern) || (second.Any(char.IsDigit) && !Regex.IsMatch(second, @"^\d{4,5}\b"));
            var firstIsPureVenue = !first.Any(char.IsDigit) && !Regex.IsMatch(first, streetPattern);

            if (firstIsPureVenue && secondLooksLikeStreet)
                AddAnchorTerms(requiredAnchors, second);
        }

        return requiredAnchors;
    }

    private static void AddAnchorTerms(List<string> anchors, string rawPart)
    {
        var cleaned = CleanEmbeddedUnits(rawPart);
        if (string.IsNullOrWhiteSpace(cleaned))
            return;

        anchors.Add(cleaned);
        foreach (var token in cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var word = token.Trim(',', '.', '#');
            if (word.Length >= 3 && !word.All(char.IsDigit))
                anchors.Add(word);
        }
    }

    private static string CleanEmbeddedUnits(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? text
            : MultipleSpacesRegex.Replace(EmbeddedUnitRegex.Replace(text, " "), " ").Trim();

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
                first.Value.TryGetProperty("lat", out var latProp) &&
                double.TryParse(lonProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) &&
                double.TryParse(latProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
            {
                return new RoutePoint(lon, lat);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Geocode lookup failed for candidate '{Candidate}'.", queryCandidate);
        }

        return null;
    }
}
