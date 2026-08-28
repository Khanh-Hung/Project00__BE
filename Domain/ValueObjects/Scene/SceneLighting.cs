namespace Domain.ValueObjects.Scene;

public sealed record SceneLighting
{
    public string Style { get; }
    public string? Direction { get; }
    public string? Temperature { get; }

    public SceneLighting(string? style = null, string? direction = null, string? temperature = null)
    {
        Style = string.IsNullOrWhiteSpace(style) ? "ambient cinematic lighting" : style.Trim();
        Direction = string.IsNullOrWhiteSpace(direction) ? null : direction.Trim();
        Temperature = string.IsNullOrWhiteSpace(temperature) ? null : temperature.Trim();
    }

    public override string ToString()
    {
        var parts = new List<string> { Style };
        if (!string.IsNullOrEmpty(Direction)) parts.Add($"from {Direction}");
        if (!string.IsNullOrEmpty(Temperature)) parts.Add(Temperature);
        return string.Join(", ", parts);
    }
}
