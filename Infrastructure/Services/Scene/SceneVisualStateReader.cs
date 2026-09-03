using System.Text.Json;
using Application.Interfaces;
using Domain.Entities;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.Scene;

public sealed class SceneVisualStateReader : ISceneVisualStateReader
{
    private readonly CoreDbContext _dbContext;
    private readonly ILogger<SceneVisualStateReader> _logger;

    public SceneVisualStateReader(CoreDbContext dbContext, ILogger<SceneVisualStateReader> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SceneVisualState?> GetLatestBySessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var record = await _dbContext.SceneVisualStates
            .AsNoTracking()
            .Where(r => r.SessionId == sessionId && r.ValidUntilTurnId == null)
            .OrderByDescending(r => r.SceneRevision)
            .ThenByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (record == null) return null;

        return JsonSerializer.Deserialize<SceneVisualState>(record.StateJson);
    }

    public async Task<SceneVisualState?> GetLatestBySessionAndSceneKeyAsync(Guid sessionId, string sceneKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sceneKey)) return null;
        var normKey = sceneKey.Trim().ToLowerInvariant();

        var record = await _dbContext.SceneVisualStates
            .AsNoTracking()
            .Where(r => r.SessionId == sessionId && r.SceneKey == normKey)
            .OrderByDescending(r => r.SceneRevision)
            .ThenByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (record == null) return null;

        return JsonSerializer.Deserialize<SceneVisualState>(record.StateJson);
    }

    public async Task<SceneVisualState?> GetLatestByCharacterIdAsync(Guid characterId, CancellationToken ct = default)
    {
        if (characterId == Guid.Empty) return null;

        var record = await _dbContext.SceneVisualStates
            .AsNoTracking()
            .Where(r => r.CharacterId == characterId && r.ValidUntilTurnId == null)
            .OrderByDescending(r => r.SceneRevision)
            .ThenByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (record == null) return null;

        return JsonSerializer.Deserialize<SceneVisualState>(record.StateJson);
    }

    public async Task SaveStateAsync(SceneVisualState state, uint expectedVersion = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state, nameof(state));

        var stateJson = JsonSerializer.Serialize(state);
        var existingRecord = await _dbContext.SceneVisualStates
            .FirstOrDefaultAsync(r => r.SessionId == state.SessionId && r.SceneKey == state.SceneKey, ct);

        if (existingRecord == null)
        {
            // Initial insert for this (SessionId, SceneKey)
            var newRecord = new SceneVisualStateRecord(
                sessionId: state.SessionId,
                characterId: state.CharacterId,
                sceneKey: state.SceneKey,
                sceneRevision: state.SceneRevision,
                stateJson: stateJson,
                fingerprint: state.Fingerprint,
                sourceTurnId: state.SourceTurnId,
                validFromTurnId: state.ValidFromTurnId,
                validUntilTurnId: state.ValidUntilTurnId,
                version: 1,
                now: state.CreatedAt
            );

            try
            {
                await _dbContext.SceneVisualStates.AddAsync(newRecord, ct);
                await _dbContext.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                // Unique constraint violation on (SessionId, SceneKey) due to race condition
                _logger.LogWarning(ex, "[SceneVisualStateReader] Concurrent insert conflict for SessionId={SessionId}, SceneKey={SceneKey}", state.SessionId, state.SceneKey);
                throw new DbUpdateConcurrencyException("Concurrent worker inserted authoritative scene state record.", ex);
            }
        }
        else
        {
            // Guard: Older Scene Revision cannot overwrite Newer Authoritative Revision
            if (state.SceneRevision < existingRecord.SceneRevision)
            {
                _logger.LogWarning("[SceneVisualStateReader] Stale scene revision rejected: incoming Revision={IncomingRevision} < authoritative Revision={CurrentRevision}",
                    state.SceneRevision, existingRecord.SceneRevision);
                return;
            }

            // Authoritative CAS Update
            if (existingRecord.Version != expectedVersion)
            {
                _logger.LogWarning("[SceneVisualStateReader] Concurrency conflict: existing record Version={CurrentVersion} != expected Version={ExpectedVersion}",
                    existingRecord.Version, expectedVersion);
                throw new DbUpdateConcurrencyException($"Authoritative scene state version mismatch: current {existingRecord.Version} vs expected {expectedVersion}.");
            }

            existingRecord.UpdateState(
                newStateJson: stateJson,
                newFingerprint: state.Fingerprint,
                newRevision: state.SceneRevision,
                turnId: state.SourceTurnId ?? Guid.NewGuid(),
                newVersion: existingRecord.Version + 1
            );

            await _dbContext.SaveChangesAsync(ct);
        }
    }
}
