namespace Domain.ValueObjects;

public sealed record VoiceExpression(
    double Rate = 1.0,
    double Pitch = 0.0,
    string Volume = "Standard",
    string EmotionTag = "Neutral"
);
