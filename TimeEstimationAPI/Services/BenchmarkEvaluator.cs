using TimeEstimationAPI.Models;

namespace TimeEstimationAPI.Services;

/// <summary>Calculates the difference between the model and provider durations.</summary>
public sealed class BenchmarkEvaluator
{
    /// <summary>Builds the benchmark values returned with the estimate.</summary>
    public BenchmarkComparison Evaluate(
        double predictedSeconds,
        IReadOnlyCollection<RouteProviderData> providers,
        double? googleSeconds)
    {
        var valhallaSeconds = GetDuration(providers, "Valhalla");
        var osrmSeconds = GetDuration(providers, "OSRM");
        var comparisonSeconds = googleSeconds ?? osrmSeconds ?? valhallaSeconds;

        return new BenchmarkComparison
        {
            OsmSeconds = osrmSeconds,
            ValhallaSeconds = valhallaSeconds,
            OsrmSeconds = osrmSeconds,
            GoogleSeconds = googleSeconds,
            AbsoluteErrorSeconds = CalculateAbsoluteError(predictedSeconds, comparisonSeconds),
            RelativeErrorPercent = CalculateRelativeError(predictedSeconds, comparisonSeconds),
            ModelVsValhallaAbsoluteErrorSeconds = CalculateAbsoluteError(predictedSeconds, valhallaSeconds),
            ModelVsValhallaRelativeErrorPercent = CalculateRelativeError(predictedSeconds, valhallaSeconds),
            ModelVsOsrmAbsoluteErrorSeconds = CalculateAbsoluteError(predictedSeconds, osrmSeconds),
            ModelVsOsrmRelativeErrorPercent = CalculateRelativeError(predictedSeconds, osrmSeconds)
        };
    }

    private static double? GetDuration(
        IEnumerable<RouteProviderData> providers,
        string providerName) =>
        providers.FirstOrDefault(provider =>
            string.Equals(provider.Provider, providerName, StringComparison.OrdinalIgnoreCase) &&
            provider.IsAvailable)?.DurationSeconds;

    private static double? CalculateAbsoluteError(double predictedSeconds, double? benchmarkSeconds) =>
        benchmarkSeconds.HasValue
            ? Math.Abs(predictedSeconds - benchmarkSeconds.Value)
            : null;

    private static double? CalculateRelativeError(double predictedSeconds, double? benchmarkSeconds) =>
        benchmarkSeconds is > 0
            ? CalculateAbsoluteError(predictedSeconds, benchmarkSeconds) / benchmarkSeconds.Value * 100
            : null;
}
