using Domain.Entities;

namespace Application.Interfaces;

public interface IVisualEvidenceRecorder
{
    Task<CharacterVisualMemory> RecordEvidenceAsync(
        Guid characterId,
        int visualProfileVersion,
        int sceneRevision,
        Guid artifactId,
        string? context = null,
        string? tags = null,
        float? qualityScore = null,
        float? identityScore = null,
        float? featureScore = null,
        CancellationToken ct = default);
}
