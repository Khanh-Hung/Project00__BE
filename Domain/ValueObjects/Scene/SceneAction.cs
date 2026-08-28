namespace Domain.ValueObjects.Scene;

public sealed record SceneAction
{
    public string Value { get; }

    public SceneAction(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Scene action cannot be empty.", nameof(value));

        Value = value.Trim();
    }

    public override string ToString() => Value;

    public static implicit operator string(SceneAction action) => action.Value;
    public static implicit operator SceneAction(string value) => new(value);
}
