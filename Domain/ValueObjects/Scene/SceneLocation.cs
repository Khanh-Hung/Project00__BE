namespace Domain.ValueObjects.Scene;

public sealed record SceneLocation
{
    public string Value { get; }
    public bool IsOutdoors { get; }

    public SceneLocation(string value, bool? isOutdoors = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Scene location cannot be empty.", nameof(value));

        Value = value.Trim();

        if (isOutdoors.HasValue)
        {
            IsOutdoors = isOutdoors.Value;
        }
        else
        {
            var lower = Value.ToLowerInvariant();
            var outdoorKeywords = new[] { "courtyard", "garden", "balcony", "street", "plaza", "park", "beach", "forest", "mountain", "terrace", "rooftop", "cliff", "river", "lake", "ocean", "valley" };
            IsOutdoors = outdoorKeywords.Any(k => lower.Contains(k));
        }
    }

    public override string ToString() => Value;

    public static implicit operator string(SceneLocation location) => location.Value;
    public static implicit operator SceneLocation(string value) => new(value);
}
