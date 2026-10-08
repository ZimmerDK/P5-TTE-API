namespace TimeEstimationAPI.Models;

/// <summary>Compares the model estimate with benchmark travel times.</summary>
public sealed class BenchmarkComparison
{
    public double? OsmSeconds { get; set; }
    public double? ValhallaSeconds { get; set; }
    public double? OsrmSeconds { get; set; }
    public double? GoogleSeconds { get; set; }
    public double? AbsoluteErrorSeconds { get; set; }
    public double? RelativeErrorPercent { get; set; }
    public double? ModelVsValhallaAbsoluteErrorSeconds { get; set; }
    public double? ModelVsValhallaRelativeErrorPercent { get; set; }
    public double? ModelVsOsrmAbsoluteErrorSeconds { get; set; }
    public double? ModelVsOsrmRelativeErrorPercent { get; set; }
}

/// <summary>Contains a route travel-time estimate and diagnostic features.</summary>
public sealed class RouteResponse
{
    public double EstimatedTravelTimeSeconds { get; set; }
    public double DistanceMeters { get; set; }
    public double Confidence { get; set; }
    public string ModelName { get; set; } = "route_time_model_v1";
    public BenchmarkComparison BenchmarkComparison { get; set; } = new();
    public UnifiedRoute? Route { get; set; }
    public Dictionary<string, object> Features { get; set; } = new();
}
