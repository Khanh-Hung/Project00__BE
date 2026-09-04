using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.CognitiveCycle;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.CognitiveCycle;

public sealed class CharacterMemoryRetrievalService : ICharacterMemoryRetrievalService
{
    private readonly CoreDbContext _dbContext;
    private readonly ILogger<CharacterMemoryRetrievalService> _logger;
    private const int DefaultMaxCount = 5;

    public CharacterMemoryRetrievalService(
        CoreDbContext dbContext,
        ILogger<CharacterMemoryRetrievalService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CharacterMemoryContext> RetrieveRelevantAsync(
        Guid characterId,
        CharacterPerceptionContext perceptionContext,
        CancellationToken ct = default)
    {
        if (characterId == Guid.Empty)
        {
            return CharacterMemoryContext.Empty;
        }

        try
        {
            // Deterministic retrieval: filter by character, order by Importance DESC, CreatedAt DESC, Id ASC
            var memories = await _dbContext.CharacterMemories
                .AsNoTracking()
                .Where(m => m.CharacterId == characterId && !m.IsSoftDeleted)
                .OrderByDescending(m => m.Importance)
                .ThenByDescending(m => m.CreatedAt)
                .ThenBy(m => m.Id)
                .Take(DefaultMaxCount)
                .ToListAsync(ct);

            if (memories.Count == 0)
            {
                return CharacterMemoryContext.Empty;
            }

            var memoryItems = memories
                .Select(m => new CharacterMemoryItem(
                    m.Id,
                    m.Type,
                    m.Content,
                    m.Importance,
                    new DateTimeOffset(m.CreatedAt, TimeSpan.Zero)
                ))
                .ToList();

            return new CharacterMemoryContext(memoryItems);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CharacterMemoryRetrievalService] Failed to retrieve memories for CharacterId={CharacterId}. Gracefully degrading to empty memory context.", characterId);
            return CharacterMemoryContext.Empty;
        }
    }
}
