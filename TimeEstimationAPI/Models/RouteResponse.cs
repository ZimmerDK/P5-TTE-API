namespace TimeEstimationAPI.Models;

/// <summary>Contains a route travel-time estimate and diagnostic features.</summary>
public sealed class RouteResponse
{
    public double ModelRouteTimeSeconds { get; set; }
    public double? ProviderRouteTimeSeconds { get; set; }
    public string? RouteProvider { get; set; }
    public double RouteDistanceM { get; set; }
    public string ModelName { get; set; } = "route_time_model_v1";
    public UnifiedRoute? Route { get; set; }
    public Dictionary<string, object> Features { get; set; } = new();
}
