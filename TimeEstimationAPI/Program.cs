using TimeEstimationAPI.Models;
using TimeEstimationAPI.Services;
using TimeEstimationAPI.Middleware;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.Configure<RouteModelOptions>(builder.Configuration.GetSection("Model"));
builder.Services.Configure<OsmOptions>(builder.Configuration.GetSection("Osm"));
builder.Services.Configure<ValhallaOptions>(builder.Configuration.GetSection("Valhalla"));
builder.Services.Configure<ContextOptions>(builder.Configuration.GetSection("Context"));

// This client is used for both Nominatim/OSM geocoding and the OSRM route call.
// The public services are fine for occasional manual tests, not heavy polling for actual "real" use.
builder.Services.AddHttpClient<OsrmService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TimeEstimationAPI/1.0 (development)");
});

builder.Services.AddHttpClient<ValhallaService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TimeEstimationAPI/1.0");
});

builder.Services.AddHttpClient<OsmTagEnricher>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TimeEstimationAPI/1.0 (development)");
});

builder.Services.AddHttpClient<TrafficContextEnricher>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ContextOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TimeEstimationAPI/1.0 (development)");
});

builder.Services.AddHttpClient<PythonModelRunner>();
builder.Services.AddHttpClient<IGeocodingService, GeocodingService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TimeEstimationAPI/1.0 (development)");
});

// Keep the rest of the app independent from the actual route-data source.
// Development uses stable local data; setting Osm:UseOsrm switches to live calls.
builder.Services.AddSingleton<IEnumerable<IRouteProvider>>(serviceProvider =>
{
    var osmOptions = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<OsmOptions>>().Value;
    var valhallaOptions = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ValhallaOptions>>().Value;
    var providers = new List<IRouteProvider>();

    if (valhallaOptions.Enabled)
        providers.Add(serviceProvider.GetRequiredService<ValhallaService>());

    providers.Add(osmOptions.UseOsrm
        ? serviceProvider.GetRequiredService<OsrmService>()
        : new DevelopmentRouteProvider());
    return providers;
});
builder.Services.AddTransient<RouteDataCollector>();
builder.Services.AddSingleton<FeatureTransformer>();
builder.Services.AddSingleton<TravelTimeModelRunner>();
builder.Services.AddSingleton<ITravelTimeModelRunner>(serviceProvider =>
{
    var options = serviceProvider
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<RouteModelOptions>>().Value;
    if (options.UsePythonService && !options.UsePlaceholder)
        return serviceProvider.GetRequiredService<PythonModelRunner>();
    if (!options.UsePlaceholder)
        throw new InvalidOperationException(
            "No model backend is configured. Enable Model:UsePythonService or Model:UsePlaceholder.");

    return serviceProvider.GetRequiredService<TravelTimeModelRunner>();
});
builder.Services.AddTransient<ModelService>();

var app = builder.Build();

// Convert known failures into Problem Details responses.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    // OpenAPI is exposed locally while developing.
    app.MapOpenApi();
}

// Main route estimate endpoint: collect data, run the model, return the estimate and route used.
app.MapPost("/api/routes/estimate", async (RouteRequest request, ModelService modelService, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    if (request.Segments.Count == 0 &&
        (string.IsNullOrWhiteSpace(request.Source) || string.IsNullOrWhiteSpace(request.Destination)))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["route"] = ["Provide segments, or provide both source and destination."]
        });
    }
    if (request.Via.Any(string.IsNullOrWhiteSpace))
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["via"] = ["Via locations must not be empty."]
        });

    var startedAt = Stopwatch.GetTimestamp();
    var result = await modelService.EstimateAsync(request, cancellationToken);
    logger.LogInformation("Estimated route with {SegmentCount} segments in {ElapsedMilliseconds} ms.",
        result.Route?.Segments.Count ?? request.Segments.Count, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
    return Results.Ok(result);
})
.WithName("EstimateRoute")
.WithSummary("Estimates route travel time.")
.WithDescription("Estimates travel time for supplied route segments or a route obtained from a configured provider.")
.Produces<RouteResponse>(StatusCodes.Status200OK)
.ProducesValidationProblem()
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status502BadGateway)
.ProducesProblem(StatusCodes.Status500InternalServerError);

app.MapPost("/api/routes/provider-data", async (
    RouteRequest request,
    RouteDataCollector routeDataCollector,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Source) || string.IsNullOrWhiteSpace(request.Destination))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["route"] = ["Provide both source and destination for provider data."]
        });
    }

    if (request.Via.Any(string.IsNullOrWhiteSpace))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["via"] = ["Via locations must not be empty."]
        });
    }

    var route = await routeDataCollector.CollectAsync(request, cancellationToken);
    return Results.Ok(route);
})
.WithName("GetRouteProviderData")
.WithSummary("Gets provider route data without running the model.")
.WithDescription("Geocodes the route, queries the configured routing provider, enriches OSRM segments with OSM tags, and returns provider data only.")
.Produces<UnifiedRoute>(StatusCodes.Status200OK)
.ProducesValidationProblem()
.ProducesProblem(StatusCodes.Status502BadGateway)
.ProducesProblem(StatusCodes.Status500InternalServerError);

app.MapPost("/api/routes/model-only", async (
    RouteRequest request,
    ModelService modelService,
    CancellationToken cancellationToken) =>
{
    if (request.Via.Any(string.IsNullOrWhiteSpace))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["via"] = ["Via locations must not be empty."]
        });
    }

    var result = await modelService.EstimateSegmentsAsync(request, cancellationToken);
    return Results.Ok(result);
})
.WithName("EstimateModelFromSegments")
.WithSummary("Estimates travel time from supplied segments using only the model.")
.WithDescription("Transforms explicit segment features and calls the configured Python model service without querying a route provider.")
.Produces<RouteResponse>(StatusCodes.Status200OK)
.ProducesValidationProblem()
.ProducesProblem(StatusCodes.Status502BadGateway)
.ProducesProblem(StatusCodes.Status500InternalServerError);

// Basic liveness check for local runs and Postman.
app.MapGet("/health", (CancellationToken _) => Results.Ok(new { status = "ok" }))
    .WithName("Health")
    .WithSummary("Checks API health.")
    .Produces(StatusCodes.Status200OK);

app.Run();

public partial class Program;
