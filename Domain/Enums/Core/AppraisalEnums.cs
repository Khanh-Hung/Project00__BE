namespace Domain.Enums;

public enum AppraisalType
{
    PhysicalDeprivation,
    PhysicalRestoration,
    Fatigue,
    Recovery,
    SocialDeprivation,
    SocialConnection,
    StressPressure,
    Safety,
    Comfort,
    Discomfort,
    PositiveMood,
    NegativeMood
}

public enum AppraisalPolarity
{
    Negative = 0,
    Neutral = 1,
    Positive = 2
}

public enum AppraisalSource
{
    Hunger,
    Energy,
    Stress,
    SocialNeed,
    Comfort,
    Mood
}
