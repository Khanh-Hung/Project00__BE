namespace Domain.ValueObjects.Scene;

public sealed record SceneCamera
{
    public string ShotType { get; }
    public string Angle { get; }
    public string Framing { get; }

    public SceneCamera(string? shotType = null, string? angle = null, string? framing = null)
    {
        ShotType = string.IsNullOrWhiteSpace(shotType) ? "medium cinematic shot" : shotType.Trim();
        Angle = string.IsNullOrWhiteSpace(angle) ? "eye-level" : angle.Trim();
        Framing = string.IsNullOrWhiteSpace(framing) ? "centered subject placement" : framing.Trim();
    }

    public override string ToString() => $"{ShotType}, {Angle} angle, {Framing}";
}
