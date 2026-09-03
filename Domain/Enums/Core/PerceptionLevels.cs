namespace Domain.Enums;

public enum HungerLevel
{
    Satisfied = 1,
    SlightlyHungry = 2,
    Hungry = 3,
    VeryHungry = 4,
    Starving = 5
}

public enum EnergyLevel
{
    Exhausted = 1,
    Tired = 2,
    Moderate = 3,
    Energized = 4,
    HighlyEnergized = 5
}

public enum StressLevel
{
    Calm = 1,
    MildPressure = 2,
    Stressed = 3,
    HighlyStressed = 4,
    Overwhelmed = 5
}

public enum SocialNeedLevel
{
    SociallySatisfied = 1,
    MildSocialNeed = 2,
    WantsCompany = 3,
    StrongNeedForCompany = 4,
    CravesConnection = 5
}

public enum ComfortLevel
{
    VeryUncomfortable = 1,
    Uncomfortable = 2,
    Neutral = 3,
    Comfortable = 4,
    VeryComfortable = 5
}

public enum MoodPerceptionLevel
{
    Depressed = 1,
    Low = 2,
    Neutral = 3,
    Good = 4,
    Elated = 5
}

public enum DominantNeed
{
    None = 0,
    Hunger = 1,
    Energy = 2,
    SocialNeed = 3,
    Comfort = 4,
    Stress = 5
}
