namespace Domain.ValueObjects.Scene;

public sealed record SceneWeather
{
    public string Value { get; }

    public SceneWeather(string? value = null)
    {
        Value = string.IsNullOrWhiteSpace(value) ? "clear" : value.Trim();
    }

    public override string ToString() => Value;

    public static implicit operator string(SceneWeather weather) => weather.Value;
    public static implicit operator SceneWeather(string? value) => new(value);
}
