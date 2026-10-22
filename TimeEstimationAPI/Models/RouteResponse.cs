namespace TimeEstimationAPI.Models;

/// <summary>Contains a route travel-time estimate and diagnostic features.</summary>
public sealed class RouteResponse
{
    public double EstimatedTravelTimeSeconds { get; set; }
    public double? ProviderEstimatedTravelTimeSeconds { get; set; }
    public string? ProviderName { get; set; }
    public double DistanceMeters { get; set; }
    public string ModelName { get; set; } = "route_time_model_v1";
    public UnifiedRoute? Route { get; set; }
    public Dictionary<string, object> Features { get; set; } = new();
}
