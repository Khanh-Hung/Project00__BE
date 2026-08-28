namespace Domain.Enums;

/// <summary>
/// Bounded set of activities a Character can perform autonomously or via interaction.
/// </summary>
public enum CharacterActivityType
{
    Idle = 0,
    Reading = 1,
    Eating = 2,
    Drinking = 3,
    Sleeping = 4,
    Cooking = 5,
    Working = 6,
    Walking = 7,
    Exercising = 8,
    Relaxing = 9,
    Bathing = 10,
    GettingReady = 11,
    Exploring = 12,
    Socializing = 13,
    Custom = 99
}
