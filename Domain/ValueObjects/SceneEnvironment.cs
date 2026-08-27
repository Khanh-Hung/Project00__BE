using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Domain.ValueObjects;

public sealed record SceneEnvironment
{
    public string Location { get; init; }
    public string? Architecture { get; init; }
    public ImmutableArray<string> BackgroundElements { get; init; }
    public ImmutableArray<string> ForegroundElements { get; init; }
    public ImmutableArray<string> Props { get; init; }
    public string? Weather { get; init; }
    public string? TimeOfDay { get; init; }
    public string? Lighting { get; init; }
    public string? Atmosphere { get; init; }

    [JsonConstructor]
    public SceneEnvironment(
        string location,
        string? architecture = null,
        ImmutableArray<string> backgroundElements = default,
        ImmutableArray<string> foregroundElements = default,
        ImmutableArray<string> props = default,
        string? weather = null,
        string? timeOfDay = null,
        string? lighting = null,
        string? atmosphere = null)
    {
        if (string.IsNullOrWhiteSpace(location))
            throw new ArgumentException("Environment location cannot be empty.", nameof(location));

        Location = location.Trim();
        Architecture = architecture?.Trim();
        BackgroundElements = backgroundElements.IsDefaultOrEmpty
            ? ImmutableArray<string>.Empty
            : backgroundElements.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToImmutableArray();
        ForegroundElements = foregroundElements.IsDefaultOrEmpty
            ? ImmutableArray<string>.Empty
            : foregroundElements.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToImmutableArray();
        Props = props.IsDefaultOrEmpty
            ? ImmutableArray<string>.Empty
            : props.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToImmutableArray();
        Weather = weather?.Trim();
        TimeOfDay = timeOfDay?.Trim();
        Lighting = lighting?.Trim();
        Atmosphere = atmosphere?.Trim();
    }

    public static SceneEnvironment Create(
        string location,
        string? architecture = null,
        IEnumerable<string>? backgroundElements = null,
        IEnumerable<string>? foregroundElements = null,
        IEnumerable<string>? props = null,
        string? weather = null,
        string? timeOfDay = null,
        string? lighting = null,
        string? atmosphere = null)
    {
        return new SceneEnvironment(
            location: location,
            architecture: architecture,
            backgroundElements: backgroundElements != null ? backgroundElements.ToImmutableArray() : ImmutableArray<string>.Empty,
            foregroundElements: foregroundElements != null ? foregroundElements.ToImmutableArray() : ImmutableArray<string>.Empty,
            props: props != null ? props.ToImmutableArray() : ImmutableArray<string>.Empty,
            weather: weather,
            timeOfDay: timeOfDay,
            lighting: lighting,
            atmosphere: atmosphere
        );
    }
}
