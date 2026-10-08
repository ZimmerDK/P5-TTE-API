using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace TimeEstimationAPI.Services;

/// <summary>Controls optional downloads of configured OSM extracts.</summary>
public sealed class MapCacheOptions
{
    public bool Enabled { get; set; }
    public string Directory { get; set; } = "map-cache";
    public int UpdateIntervalHours { get; set; } = 168;
    public List<MapRegionDefinition> Regions { get; set; } = [];
}

public sealed class MapRegionDefinition
{
    public required string Name { get; set; }
    public required string Url { get; set; }
}

public interface IMapRegionCache
{
    Task RefreshAsync(CancellationToken cancellationToken);
}

/// <summary>Downloads configured PBF files outside the request path.</summary>
public sealed class MapRegionCache(
    IHttpClientFactory httpClientFactory,
    IOptions<MapCacheOptions> options,
    ILogger<MapRegionCache> logger) : IMapRegionCache
{
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled || settings.Regions.Count == 0)
            return;

        Directory.CreateDirectory(settings.Directory);
        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(30);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TimeEstimationAPI", "1.0"));

        foreach (var region in settings.Regions)
        {
            var target = Path.Combine(settings.Directory, $"{region.Name}.osm.pbf");
            if (File.Exists(target) &&
                DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(target) <
                TimeSpan.FromHours(Math.Max(settings.UpdateIntervalHours, 1)))
                continue;

            logger.LogInformation("Downloading configured map region {Region}.", region.Name);
            using var response = await client.GetAsync(region.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            var temporaryTarget = $"{target}.{Guid.NewGuid():N}.download";
            try
            {
                await using var output = File.Create(temporaryTarget);
                await input.CopyToAsync(output, cancellationToken);
                File.Move(temporaryTarget, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryTarget))
                    File.Delete(temporaryTarget);
            }
        }
    }
}

/// <summary>Runs map extract refreshes on a schedule, never during route requests.</summary>
public sealed class MapRegionUpdateService(
    IMapRegionCache cache,
    IOptions<MapCacheOptions> options,
    ILogger<MapRegionUpdateService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
            return;

        await RefreshSafelyAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Max(options.Value.UpdateIntervalHours, 1)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RefreshSafelyAsync(stoppingToken);
    }

    private async Task RefreshSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await cache.RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (HttpRequestException exception)
        {
            logger.LogError(exception, "Map region refresh failed.");
        }
        catch (IOException exception)
        {
            logger.LogError(exception, "Map region cache write failed.");
        }
    }
}
