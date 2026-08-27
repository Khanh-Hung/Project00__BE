using Application.Interfaces;
using Domain.Entities;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services.Scene;

public sealed class PreviousSceneReader : IPreviousSceneReader
{
    private readonly ProjectDbContext _dbContext;

    public PreviousSceneReader(ProjectDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<SceneSpecification?> GetLatestSceneBySessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        return await _dbContext.SceneSpecifications
            .AsNoTracking()
            .Where(s => s.SessionId == sessionId)
            .OrderByDescending(s => s.SceneRevision)
            .ThenByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<SceneSpecification?> GetSceneByTurnAsync(Guid sessionId, Guid turnId, CancellationToken ct = default)
    {
        return await _dbContext.SceneSpecifications
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.TurnId == turnId);
    }
}
