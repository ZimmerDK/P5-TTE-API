namespace TimeEstimationAPI.Models;

public sealed class RouteFeatureVector
{
    public double[][] Sequence { get; set; } = [];
    public double[] TimeFeatures { get; set; } = Array.Empty<double>();
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> SegmentFeatures { get; set; } = [];
    public IReadOnlyDictionary<string, object?> RouteFeatures { get; set; } =
        new Dictionary<string, object?>();
    public string ModelName { get; set; } = "route_time_model_v1";
}
