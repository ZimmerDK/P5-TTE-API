namespace TimeEstimationAPI.Models;

public sealed class RouteFeatureVector
{
    public double[][] Sequence { get; set; } = [];
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> SegmentFeatures { get; set; } = [];
    public string ModelName { get; set; } = "route_time_model_v1";
}
