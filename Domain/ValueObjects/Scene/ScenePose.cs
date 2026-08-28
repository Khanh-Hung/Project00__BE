namespace Domain.ValueObjects.Scene;

public sealed record ScenePose
{
    public string Value { get; }

    public ScenePose(string value)
    {
        Value = string.IsNullOrWhiteSpace(value) ? "standing naturally" : value.Trim();
    }

    public override string ToString() => Value;

    public static implicit operator string(ScenePose pose) => pose.Value;
    public static implicit operator ScenePose(string value) => new(value);
}
