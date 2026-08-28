namespace Domain.ValueObjects.Scene;

public sealed record SceneTimeOfDay
{
    public string Value { get; }

    public SceneTimeOfDay(string? value = null)
    {
        Value = string.IsNullOrWhiteSpace(value) ? "daytime" : value.Trim();
    }

    public override string ToString() => Value;

    public static implicit operator string(SceneTimeOfDay time) => time.Value;
    public static implicit operator SceneTimeOfDay(string? value) => new(value);
}
