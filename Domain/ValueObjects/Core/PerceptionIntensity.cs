namespace Domain.ValueObjects;

public readonly record struct PerceptionIntensity
{
    public double Value { get; }

    public PerceptionIntensity(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentException("Perception intensity must be a finite real number.", nameof(value));
        }

        if (value < 0.0 || value > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Perception intensity must be bounded in [0.0, 1.0].");
        }

        Value = Math.Round(value, 4);
    }

    public static implicit operator double(PerceptionIntensity intensity) => intensity.Value;
    public static explicit operator PerceptionIntensity(double value) => new(value);

    public override string ToString() => Value.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
}
