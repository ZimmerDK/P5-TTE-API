using System.ComponentModel.DataAnnotations;

namespace TimeEstimationAPI.Models;

/// <summary>Describes a route for which travel time should be estimated.</summary>
public sealed record RouteRequest
{
    [Required, MinLength(1), MaxLength(200)]
    public string Source { get; init; } = string.Empty;

    [Required, MinLength(1), MaxLength(200)]
    public string Destination { get; init; } = string.Empty;

    [MaxLength(50)]
    public List<string> Waypoints { get; init; } = new();

    public DateTimeOffset DepartureTime { get; init; } = DateTimeOffset.UtcNow;

    [Required]
    public string Mode { get; init; } = "car";
}
