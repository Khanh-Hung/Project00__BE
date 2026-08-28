using System.Text.Json;
using Application.Interfaces;
using Domain.Entities;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.Scene;

public sealed class SceneVisualStateReader : ISceneVisualStateReader
{
    private readonly ProjectDbContext _dbContext;
    private readonly ILogger<SceneVisualStateReader> _logger;

    public SceneVisualStateReader(ProjectDbContext dbContext, ILogger<SceneVisualStateReader> logger)
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

        try
        {
            return JsonSerializer.Deserialize<SceneVisualState>(record.StateJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SceneVisualStateReader] Failed to deserialize SceneVisualState from record Id={RecordId}", record.Id);
            return null;
        }
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

        try
        {
            return JsonSerializer.Deserialize<SceneVisualState>(record.StateJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SceneVisualStateReader] Failed to deserialize SceneVisualState from record Id={RecordId}", record.Id);
            return null;
        }
    }

    public async Task SaveStateAsync(SceneVisualState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state, nameof(state));

        var stateJson = JsonSerializer.Serialize(state);
        var record = new SceneVisualStateRecord(
            sessionId: state.SessionId,
            characterId: state.CharacterId,
            sceneKey: state.SceneKey,
            sceneRevision: state.SceneRevision,
            stateJson: stateJson,
            fingerprint: state.Fingerprint,
            sourceTurnId: state.SourceTurnId,
            validFromTurnId: state.ValidFromTurnId,
            validUntilTurnId: state.ValidUntilTurnId,
            version: state.Version,
            now: state.CreatedAt
        );

        await _dbContext.SceneVisualStates.AddAsync(record, ct);
        await _dbContext.SaveChangesAsync(ct);
    }
}
