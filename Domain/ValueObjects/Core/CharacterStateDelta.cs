namespace Domain.ValueObjects;

public sealed record CharacterStateDelta
{
    public decimal HungerDelta { get; init; }
    public decimal EnergyDelta { get; init; }
    public decimal MoodDelta { get; init; }
    public decimal StressDelta { get; init; }
    public decimal SocialNeedDelta { get; init; }
    public decimal ComfortDelta { get; init; }

    public static CharacterStateDelta Zero { get; } = new(0, 0, 0, 0, 0, 0);

    public bool IsZero =>
        HungerDelta == 0m &&
        EnergyDelta == 0m &&
        MoodDelta == 0m &&
        StressDelta == 0m &&
        SocialNeedDelta == 0m &&
        ComfortDelta == 0m;

    public CharacterStateDelta() { }

    public CharacterStateDelta(
        decimal hungerDelta = 0m,
        decimal energyDelta = 0m,
        decimal moodDelta = 0m,
        decimal stressDelta = 0m,
        decimal socialNeedDelta = 0m,
        decimal comfortDelta = 0m)
    {
        ValidateNumber(hungerDelta, nameof(hungerDelta));
        ValidateNumber(energyDelta, nameof(energyDelta));
        ValidateNumber(moodDelta, nameof(moodDelta));
        ValidateNumber(stressDelta, nameof(stressDelta));
        ValidateNumber(socialNeedDelta, nameof(socialNeedDelta));
        ValidateNumber(comfortDelta, nameof(comfortDelta));

        HungerDelta = hungerDelta;
        EnergyDelta = energyDelta;
        MoodDelta = moodDelta;
        StressDelta = stressDelta;
        SocialNeedDelta = socialNeedDelta;
        ComfortDelta = comfortDelta;
    }

    private static void ValidateNumber(decimal value, string paramName)
    {
        // decimal in .NET cannot be NaN or Infinity, but doubles/floats converted might be or if checked
    }

    public static CharacterStateDelta Create(
        double hungerDelta = 0,
        double energyDelta = 0,
        double moodDelta = 0,
        double stressDelta = 0,
        double socialNeedDelta = 0,
        double comfortDelta = 0)
    {
        if (double.IsNaN(hungerDelta) || double.IsInfinity(hungerDelta))
            throw new ArgumentException("HungerDelta cannot be NaN or Infinity.", nameof(hungerDelta));
        if (double.IsNaN(energyDelta) || double.IsInfinity(energyDelta))
            throw new ArgumentException("EnergyDelta cannot be NaN or Infinity.", nameof(energyDelta));
        if (double.IsNaN(moodDelta) || double.IsInfinity(moodDelta))
            throw new ArgumentException("MoodDelta cannot be NaN or Infinity.", nameof(moodDelta));
        if (double.IsNaN(stressDelta) || double.IsInfinity(stressDelta))
            throw new ArgumentException("StressDelta cannot be NaN or Infinity.", nameof(stressDelta));
        if (double.IsNaN(socialNeedDelta) || double.IsInfinity(socialNeedDelta))
            throw new ArgumentException("SocialNeedDelta cannot be NaN or Infinity.", nameof(socialNeedDelta));
        if (double.IsNaN(comfortDelta) || double.IsInfinity(comfortDelta))
            throw new ArgumentException("ComfortDelta cannot be NaN or Infinity.", nameof(comfortDelta));

        return new CharacterStateDelta(
            (decimal)Math.Round(hungerDelta, 2),
            (decimal)Math.Round(energyDelta, 2),
            (decimal)Math.Round(moodDelta, 2),
            (decimal)Math.Round(stressDelta, 2),
            (decimal)Math.Round(socialNeedDelta, 2),
            (decimal)Math.Round(comfortDelta, 2)
        );
    }
}
