using TimeEstimationAPI.Models;

namespace TimeEstimationAPI.Services;

/// <summary>Maps the unified route into the feature layout expected by the model.</summary>
public sealed class FeatureTransformer
{
    /// <summary>Builds numeric route and time features from the collected route.</summary>
    public RouteFeatureVector Transform(UnifiedRoute route)
    {
        // The model cannot use a clock value directly. Sine/cosine keeps
        // 23:00 close to 00:00 instead of treating them as far apart.
        var localTime = route.DepartureTime.ToLocalTime();

        var hour = localTime.Hour + localTime.Minute / 60.0;
        var minute = localTime.Minute + localTime.Second / 60.0;

        var timeFeatures = new[]
        {
            Math.Sin((2 * Math.PI * hour) / 24.0),
            Math.Cos((2 * Math.PI * hour) / 24.0),
            Math.Sin((2 * Math.PI * minute) / 60.0),
            Math.Cos((2 * Math.PI * minute) / 60.0)
        };

        // These numbers are category codes, not real road measurements.
        // The final model training code must use the same mapping.
        var roadTypeCode = route.RoadType switch
        {
            "motorway" => 3.0,
            "secondary" => 2.0,
            _ => 1.0
        };

        // Keep this order stable. It is the contract between this API and the model.
        // Values are scaled where noted so one large number does not dominate.
        var featureVector = new[]
        {
            (route.DistanceMeters ?? 0) / 1000.0, // distance, metres -> kilometres
            (route.SpeedLimitKmh ?? 0) / 120.0, // speed, scaled against 120 km/h
            roadTypeCode,
            (route.TrafficSignalCount ?? 0) / 10.0, // signal count, scaled
            1.0, // urban flag placeholder: 1 means "urban" for now
            0.5, // congestion placeholder: 0 = none, 1 = heavy
            ((route.SpeedLimitKmh ?? 0) * 0.75) / 100.0, // rough historical speed
            1.0, // weather factor placeholder: 1 means neutral weather
            route.MissingData.Count > 0 ? 1.0 : 0.0, // any source data missing
            route.Waypoints.Count // kept as a count for the current model
        };

        return new RouteFeatureVector
        {
            Features = featureVector,
            TimeFeatures = timeFeatures,
            ModelName = "route_time_model_v1"
        };
    }
}
