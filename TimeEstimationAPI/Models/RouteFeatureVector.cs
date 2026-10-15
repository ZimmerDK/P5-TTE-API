namespace TimeEstimationAPI.Models;

public sealed class RouteFeatureVector
{
    public double[][] Sequence { get; set; } = [];
    public double[] TimeFeatures { get; set; } = Array.Empty<double>();
    public string ModelName { get; set; } = "route_time_model_v1";
}
