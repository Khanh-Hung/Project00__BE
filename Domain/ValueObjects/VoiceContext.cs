using Domain.Enums;

namespace Domain.ValueObjects;

public sealed record VoiceContext(
    CharacterVoiceProfile Voice,
    CharacterMood Mood,
    int MoodIntensity,
    int AffectionScore,
    string RelationshipStage,
    string RawText
);
