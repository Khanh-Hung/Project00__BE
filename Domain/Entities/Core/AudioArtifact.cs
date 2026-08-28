using Domain.Common;

namespace Domain.Entities;

/// <summary>
/// Immutable audio rendering artifact for a specific voice request within a ChatSession/CharacterTurn.
/// Key invariant: ContextHash serves as the primary idempotency identity with DB UNIQUE(ContextHash) constraint.
/// One committed CharacterTurn has at most one canonical AudioArtifact per semantic VoiceContext.
/// </summary>
public sealed class AudioArtifact : BaseEntity
{
    public Guid SessionId { get; private set; }
    public Guid CharacterId { get; private set; }
    public Guid TurnId { get; private set; }
    public Guid UserId { get; private set; }
    public string VoiceId { get; private set; } = string.Empty;
    public string CleanedText { get; private set; } = string.Empty;
    public string ContextHash { get; private set; } = string.Empty;
    public string AudioUrl { get; private set; } = string.Empty;
    public string AudioFormat { get; private set; } = "audio/mpeg";
    public TimeSpan? Duration { get; private set; }

    private AudioArtifact() { } // EF Core

    public AudioArtifact(
        Guid sessionId,
        Guid characterId,
        Guid turnId,
        Guid userId,
        string voiceId,
        string cleanedText,
        string contextHash,
        string audioUrl,
        string audioFormat = "audio/mpeg",
        TimeSpan? duration = null)
    {
        SessionId = sessionId;
        CharacterId = characterId;
        TurnId = turnId;
        UserId = userId;
        VoiceId = voiceId;
        CleanedText = cleanedText;
        ContextHash = contextHash;
        AudioUrl = audioUrl;
        AudioFormat = audioFormat;
        Duration = duration;
    }
}
