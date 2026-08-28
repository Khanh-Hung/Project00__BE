namespace Domain.ValueObjects;

public sealed record PsychologyProfile(
    string? Desires = null,
    string? Fears = null,
    string? Insecurities = null,
    string? CoreBeliefs = null,
    string? InternalConflicts = null,
    string? Values = null
);

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
