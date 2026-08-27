using Application.Interfaces;
using Domain.Entities;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class VisualEvidenceRecorder : IVisualEvidenceRecorder
{
    private readonly ProjectDbContext _dbContext;
    private readonly ILogger<VisualEvidenceRecorder> _logger;

    public VisualEvidenceRecorder(
        ProjectDbContext dbContext,
        ILogger<VisualEvidenceRecorder> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<CharacterVisualMemory> RecordEvidenceAsync(
        Guid characterId,
        int visualProfileVersion,
        int sceneRevision,
        Guid artifactId,
        string? context = null,
        string? tags = null,
        float? qualityScore = null,
        float? identityScore = null,
        float? featureScore = null,
        CancellationToken ct = default)
    {
        // Check if memory for this artifact already exists (idempotency)
        var existing = await _dbContext.CharacterVisualMemories
            .FirstOrDefaultAsync(m => m.CharacterId == characterId && m.ArtifactId == artifactId, ct);

        if (existing != null)
        {
            _logger.LogInformation("[VisualEvidenceRecorder] Visual memory already exists for CharacterId={CharacterId}, ArtifactId={ArtifactId}. Returning existing.",
                characterId, artifactId);
            return existing;
        }

        var memory = new CharacterVisualMemory(
            characterId: characterId,
            visualProfileVersion: visualProfileVersion,
            sceneRevision: sceneRevision,
            artifactId: artifactId,
            context: context,
            tags: tags,
            qualityScore: qualityScore,
            identityScore: identityScore,
            featureScore: featureScore,
            now: DateTime.UtcNow
        );

        await _dbContext.CharacterVisualMemories.AddAsync(memory, ct);
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("[VisualEvidenceRecorder] Recorded Visual Evidence/Memory for CharacterId={CharacterId}, ArtifactId={ArtifactId} (ProfileVersion={ProfileVersion}, SceneRevision={SceneRevision})",
            characterId, artifactId, visualProfileVersion, sceneRevision);

        return memory;
    }
}
