namespace Domain.ValueObjects;

public sealed record PsychologyProfile(
    string? Desires = null,
    string? Fears = null,
    string? Insecurities = null,
    string? CoreBeliefs = null,
    string? InternalConflicts = null,
    string? Values = null,
    decimal HungerSensitivity = 1.0m,
    decimal FatigueSensitivity = 1.0m,
    decimal StressSensitivity = 1.0m,
    decimal SocialSensitivity = 1.0m,
    decimal ComfortSensitivity = 1.0m,
    decimal MoodReactivity = 1.0m
)
{
    public static PsychologyProfile Default { get; } = new();
}

public sealed record BehaviorProfile(
    string? WhenHappy = null,
    string? WhenSad = null,
    string? WhenAngry = null,
    string? WhenTeased = null,
    string? WhenPraised = null,
    string? WhenRejected = null
);

public sealed record ExpressionProfile(
    string? SpeechStyle = null,
    string? Formality = null,
    string? HumorStyle = null,
    string? EmojiUsage = null,
    List<string>? TypicalPhrases = null
);

public sealed record CharacterRules(
    List<string>? MustDo = null,
    List<string>? MustNotDo = null,
    string? AntiSycophancy = null,
    List<string>? Boundaries = null
);

public sealed record CharacterBlueprint(
    PsychologyProfile? Psychology = null,
    BehaviorProfile? Behavior = null,
    ExpressionProfile? Expression = null,
    CharacterRules? Rules = null
);
