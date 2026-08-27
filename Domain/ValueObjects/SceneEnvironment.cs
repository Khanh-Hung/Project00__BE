using System.Collections.Immutable;

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

    public SceneEnvironment(
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
        if (string.IsNullOrWhiteSpace(location))
            throw new ArgumentException("Environment location cannot be empty.", nameof(location));

        Location = location.Trim();
        Architecture = architecture?.Trim();
        BackgroundElements = backgroundElements != null
            ? backgroundElements.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToImmutableArray()
            : ImmutableArray<string>.Empty;
        ForegroundElements = foregroundElements != null
            ? foregroundElements.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToImmutableArray()
            : ImmutableArray<string>.Empty;
        Props = props != null
            ? props.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToImmutableArray()
            : ImmutableArray<string>.Empty;
        Weather = weather?.Trim();
        TimeOfDay = timeOfDay?.Trim();
        Lighting = lighting?.Trim();
        Atmosphere = atmosphere?.Trim();
    }
}
