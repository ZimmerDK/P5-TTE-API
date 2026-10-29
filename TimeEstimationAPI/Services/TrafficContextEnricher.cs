using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TimeEstimationAPI.Models;

namespace TimeEstimationAPI.Services;

/// <summary>Options for the spatial context density service.</summary>
public sealed class ContextOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8010/";
    public bool Enabled { get; set; } = true;
    public int RadiusMeters { get; set; } = 500;
    public double DefaultYear { get; set; } = 2026.0;
    public string RoadNetwork { get; set; } = "nordjylland_2026";
    public string FeatureKey { get; set; } = "road_density";
}

/// <summary>Enriches route segments with spatial traffic & density context from the context microservice.</summary>
public sealed class TrafficContextEnricher(
    HttpClient httpClient,
    IOptions<ContextOptions> options,
    ILogger<TrafficContextEnricher> logger)
{
    private readonly ContextOptions _options = options.Value;

    public async Task<IReadOnlyList<RouteSegment>> EnrichAsync(
        IReadOnlyList<RouteSegment> segments,
        CancellationToken cancellationToken)
    {
        if (segments.Count == 0)
            return segments;

        if (!_options.Enabled)
        {
            logger.LogDebug("Context density enrichment is disabled in options. Applying fallback defaults.");
            return ApplyFallbackDensity(segments);
        }

        // Cache responses by coordinate rounded to 3 decimal places (~100m) to optimize calls along route sub-segments
        var cache = new Dictionary<(double Lat, double Lon), ContextMetrics?>();
        var result = new List<RouteSegment>(segments.Count);
        var reportedUnreachable = false;

        foreach (var segment in segments)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            // Preserve existing density values if provided
            if (segment.Density.HasValue && segment.RoadDensity.HasValue && segment.PopulationDensity.HasValue)
            {
                result.Add(segment);
                continue;
            }

            var point = GetSegmentMidpoint(segment);
            if (point is null)
            {
                result.Add(ApplySegmentFallback(segment));
                continue;
            }

            var roundedKey = (Math.Round(point.Latitude, 3), Math.Round(point.Longitude, 3));
            if (!cache.TryGetValue(roundedKey, out var metrics))
            {
                if (!reportedUnreachable)
                {
                    var fetchResult = await FetchContextAsync(point.Latitude, point.Longitude, cancellationToken);
                    if (fetchResult.IsUnreachable)
                    {
                        reportedUnreachable = true;
                    }
                    metrics = fetchResult.Metrics;
                }

                cache[roundedKey] = metrics;
            }

            if (metrics is not null)
            {
                var roadDensity = segment.RoadDensity ?? metrics.RoadDensity;
                var populationDensity = segment.PopulationDensity ?? metrics.PopulationDensity;
                var builtVolumeDensity = segment.BuiltVolumeDensity ?? metrics.BuiltVolumeDensity;
                var builtUpFraction = segment.BuiltUpFraction ?? metrics.BuiltUpFraction;

                var selectedDensity = segment.Density ?? (_options.FeatureKey.ToLowerInvariant() switch
                {
                    "population_density" or "population" => populationDensity ?? roadDensity ?? builtVolumeDensity ?? 0.0,
                    "built_volume_density" or "built_volume" => builtVolumeDensity ?? roadDensity ?? populationDensity ?? 0.0,
                    _ => roadDensity ?? populationDensity ?? builtVolumeDensity ?? 0.0
                });

                var missing = segment.MissingData
                    .Where(field => !string.Equals(field, "density", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                result.Add(segment with
                {
                    Density = selectedDensity,
                    RoadDensity = roadDensity,
                    PopulationDensity = populationDensity,
                    BuiltVolumeDensity = builtVolumeDensity,
                    BuiltUpFraction = builtUpFraction,
                    MissingData = missing
                });
            }
            else
            {
                result.Add(ApplySegmentFallback(segment));
            }
        }

        return result;
    }

    private static IReadOnlyList<RouteSegment> ApplyFallbackDensity(IReadOnlyList<RouteSegment> segments)
    {
        return segments.Select(ApplySegmentFallback).ToArray();
    }

    private static RouteSegment ApplySegmentFallback(RouteSegment segment)
    {
        var density = segment.Density ?? segment.RoadDensity ?? segment.PopulationDensity ?? 0.0;
        return segment with
        {
            Density = density,
            RoadDensity = segment.RoadDensity ?? density,
            PopulationDensity = segment.PopulationDensity ?? 0.0,
            BuiltVolumeDensity = segment.BuiltVolumeDensity ?? 0.0
        };
    }

    private static RoutePoint? GetSegmentMidpoint(RouteSegment segment)
    {
        if (segment.Geometry.Count == 0)
            return null;

        var midIndex = segment.Geometry.Count / 2;
        return segment.Geometry[midIndex];
    }

    private async Task<ContextFetchResult> FetchContextAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        var radiusKey = _options.RadiusMeters.ToString(CultureInfo.InvariantCulture);
        var requestUrl = $"context?lat={latitude.ToString(CultureInfo.InvariantCulture)}&lon={longitude.ToString(CultureInfo.InvariantCulture)}&year={_options.DefaultYear.ToString(CultureInfo.InvariantCulture)}&road_network={Uri.EscapeDataString(_options.RoadNetwork)}";

        try
        {
            using var response = await httpClient.GetAsync(requestUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Context endpoint returned HTTP status {StatusCode} for coordinates {Lat},{Lon}.", (int)response.StatusCode, latitude, longitude);
                // 5xx errors indicate service issues; 4xx indicates request/coordinate boundary issue
                var isUnreachable = (int)response.StatusCode >= 500;
                return new ContextFetchResult(isUnreachable, null);
            }

            using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);
            if (doc is null)
                return new ContextFetchResult(false, null);

            var root = doc.RootElement;
            double? roadDensity = null;
            double? populationDensity = null;
            double? builtVolumeDensity = null;
            double? builtUpFraction = null;

            if (root.TryGetProperty("roads", out var roadsProp) &&
                roadsProp.ValueKind == JsonValueKind.Object &&
                roadsProp.TryGetProperty("radii", out var radiiProp) &&
                radiiProp.ValueKind == JsonValueKind.Object &&
                radiiProp.TryGetProperty(radiusKey, out var radiusObj) &&
                radiusObj.ValueKind == JsonValueKind.Object)
            {
                if (radiusObj.TryGetProperty("road_density_km_per_km2", out var rdElem) && rdElem.ValueKind == JsonValueKind.Number)
                {
                    roadDensity = rdElem.GetDouble();
                }
            }

            if (root.TryGetProperty("features", out var featuresProp) &&
                featuresProp.ValueKind == JsonValueKind.Object &&
                featuresProp.TryGetProperty(radiusKey, out var featObj) &&
                featObj.ValueKind == JsonValueKind.Object)
            {
                if (featObj.TryGetProperty("population_density_people_km2", out var popElem) && popElem.ValueKind == JsonValueKind.Number)
                {
                    populationDensity = popElem.GetDouble();
                }
                if (featObj.TryGetProperty("built_volume_density_m3_per_m2", out var bvElem) && bvElem.ValueKind == JsonValueKind.Number)
                {
                    builtVolumeDensity = bvElem.GetDouble();
                }
                if (featObj.TryGetProperty("built_up_fraction", out var bufElem) && bufElem.ValueKind == JsonValueKind.Number)
                {
                    builtUpFraction = bufElem.GetDouble();
                }
            }

            return new ContextFetchResult(false, new ContextMetrics(roadDensity, populationDensity, builtVolumeDensity, builtUpFraction));
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Context service at {BaseUrl} is unreachable. Applying fallback density.", _options.BaseUrl);
            return new ContextFetchResult(true, null);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Context service request timed out. Applying fallback density.");
            return new ContextFetchResult(true, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse context service response for coordinates {Lat},{Lon}. Applying fallback density.", latitude, longitude);
            return new ContextFetchResult(false, null);
        }
    }

    private sealed record ContextFetchResult(bool IsUnreachable, ContextMetrics? Metrics);

    private sealed record ContextMetrics(
        double? RoadDensity,
        double? PopulationDensity,
        double? BuiltVolumeDensity,
        double? BuiltUpFraction);
}
