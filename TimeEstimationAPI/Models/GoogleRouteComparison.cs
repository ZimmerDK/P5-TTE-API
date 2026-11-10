namespace TimeEstimationAPI.Models;


/// <summary>Compares the optional Google route with the primary provider and model.</summary>
public sealed record GoogleRouteComparison
{
    public double? GoogleRouteTimeSeconds { get; init; }
    /// <summary>Google route time minus the model route estimate. Positive means Google is higher/slower.</summary>
    public double? GoogleVsModelDeltaSeconds { get; init; }
    /// <summary>Google route time minus the primary provider route estimate. Positive means Google is higher/slower.</summary>
    public double? GoogleVsProviderDeltaSeconds { get; init; }
    public double? GoogleDistanceMeters { get; init; }
    public IReadOnlyList<GoogleSegmentComparison> Segments { get; init; } = [];
}

/// <summary>Compares a primary route segment with a distance-weighted Google estimate.</summary>
public sealed record GoogleSegmentComparison
{
    public int Sequence { get; init; }
    public double? GoogleTimeSeconds { get; init; }
    public double? ProviderTimeSeconds { get; init; }
    public double? ModelTimeSeconds { get; init; }
    /// <summary>Google segment time minus the model segment estimate. Positive means Google is higher/slower.</summary>
    public double? GoogleVsModelDeltaSeconds { get; init; }
    /// <summary>Google segment time minus the provider segment estimate. Positive means Google is higher/slower.</summary>
    public double? GoogleVsProviderDeltaSeconds { get; init; }
}
