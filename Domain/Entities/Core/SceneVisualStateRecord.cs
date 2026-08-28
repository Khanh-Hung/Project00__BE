using Domain.Common;

namespace Domain.Entities;

/// <summary>
/// Authoritative persistence entity capturing historical and active SceneVisualState snapshots in database.
/// Provides optimistic concurrency fencing via Version token.
/// </summary>
public sealed class SceneVisualStateRecord : BaseEntity
{
    public Guid SessionId { get; private set; }
    public Guid CharacterId { get; private set; }
    public string SceneKey { get; private set; }
    public int SceneRevision { get; private set; }
    public string StateJson { get; private set; }
    public string Fingerprint { get; private set; }
    public Guid? SourceTurnId { get; private set; }
    public Guid? ValidFromTurnId { get; private set; }
    public Guid? ValidUntilTurnId { get; private set; }
    public uint Version { get; private set; } = 1;

    private SceneVisualStateRecord() 
    {
        SceneKey = null!;
        StateJson = null!;
        Fingerprint = null!;
    } // EF Core

    public SceneVisualStateRecord(
        Guid sessionId,
        Guid characterId,
        string sceneKey,
        int sceneRevision,
        string stateJson,
        string fingerprint,
        Guid? sourceTurnId = null,
        Guid? validFromTurnId = null,
        Guid? validUntilTurnId = null,
        uint version = 1,
        DateTime? now = null)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("SessionId cannot be empty.", nameof(sessionId));

        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        if (string.IsNullOrWhiteSpace(sceneKey))
            throw new ArgumentException("SceneKey cannot be empty.", nameof(sceneKey));

        if (string.IsNullOrWhiteSpace(stateJson))
            throw new ArgumentException("StateJson cannot be empty.", nameof(stateJson));

        if (string.IsNullOrWhiteSpace(fingerprint))
            throw new ArgumentException("Fingerprint cannot be empty.", nameof(fingerprint));

        if (sceneRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(sceneRevision), "SceneRevision must be >= 1.");

        Id = Guid.CreateVersion7();
        SessionId = sessionId;
        CharacterId = characterId;
        SceneKey = sceneKey.Trim().ToLowerInvariant();
        SceneRevision = sceneRevision;
        StateJson = stateJson;
        Fingerprint = fingerprint.Trim();
        SourceTurnId = sourceTurnId;
        ValidFromTurnId = validFromTurnId ?? sourceTurnId;
        ValidUntilTurnId = validUntilTurnId;
        Version = version;
        CreatedAt = now ?? DateTime.UtcNow;
    }

    public void UpdateState(string newStateJson, string newFingerprint, int newRevision, Guid turnId, uint newVersion)
    {
        StateJson = newStateJson;
        Fingerprint = newFingerprint;
        SceneRevision = newRevision;
        SourceTurnId = turnId;
        Version = newVersion;
        Touch();
    }

    public void Invalidate(Guid supersededByTurnId)
    {
        ValidUntilTurnId = supersededByTurnId;
        Version++;
        Touch();
    }
}
