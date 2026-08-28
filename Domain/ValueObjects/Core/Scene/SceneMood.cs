namespace Domain.ValueObjects.Scene;

public sealed record SceneMood
{
    public string Value { get; }

    public SceneMood(string? value = null)
    {
        Value = string.IsNullOrWhiteSpace(value) ? "neutral cinematic" : value.Trim();
    }

    public override string ToString() => Value;

    public static implicit operator string(SceneMood mood) => mood.Value;
    public static implicit operator SceneMood(string? value) => new(value);
}
