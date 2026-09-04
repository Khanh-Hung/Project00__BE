using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.CognitiveCycle;
using Domain.Entities;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.CognitiveCycle;

/// <summary>
/// Infrastructure service for recording cognitive cycle outcome feedback into the memory system.
/// 
/// Idempotency Invariant:
/// - same CharacterId + same ExecutionId + same semantic feedback = idempotent replay (existing memory reused)
/// - same CharacterId + same ExecutionId + different semantic feedback = idempotency conflict (throws CharacterMemoryIdempotencyConflictException)
/// 
/// Database-Level Uniqueness Guarantee:
/// Physical uniqueness is enforced by the Primary Key constraint on CharacterMemory.Id (PK_CharacterMemories in PostgreSQL).
/// Because MemoryId is deterministically derived via SHA-256(CharacterId + ExecutionId), any concurrent race on the same ExecutionId
/// results in a physical PK violation (DbUpdateException), which is caught and safely reconciled.
/// 
/// Semantics:
/// CharacterMemoryFeedback is an immutable result describing the persisted CharacterMemory created by the feedback operation.
/// It is not a separately persisted entity, and MemoryId identifies the resulting CharacterMemory.
/// </summary>
public sealed class CharacterMemoryFeedbackService : ICharacterMemoryFeedbackService
{
    private readonly CoreDbContext _dbContext;
    private readonly ILogger<CharacterMemoryFeedbackService> _logger;

    public CharacterMemoryFeedbackService(
        CoreDbContext dbContext,
        ILogger<CharacterMemoryFeedbackService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CharacterMemoryFeedback?> RecordFeedbackAsync(
        CharacterCognitiveCycleContext cycleContext,
        CharacterCognitiveCycleResult cycleResult,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cycleContext);
        ArgumentNullException.ThrowIfNull(cycleResult);

        // Disregard non-actionable input validation failures or not-founds
        if (cycleResult.Status is CharacterCognitiveCycleStatus.InvalidInput or CharacterCognitiveCycleStatus.NotFound)
        {
            return null;
        }

        var (feedbackType, content) = DetermineFeedback(cycleResult);
        var memoryId = CreateDeterministicMemoryId(cycleContext.CharacterId, cycleContext.ExecutionId);

        try
        {
            // Idempotency check: see if memory for this character and execution was already recorded.
            // Invariant:
            // same CharacterId + same ExecutionId + same semantic feedback = idempotent replay
            // same CharacterId + same ExecutionId + different semantic feedback = idempotency conflict
            var existing = await _dbContext.CharacterMemories
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == memoryId || (m.CharacterId == cycleContext.CharacterId && m.SourceSessionId == cycleContext.ExecutionId), ct);

            if (existing != null)
            {
                // Validate semantic consistency
                if (existing.CharacterId != cycleContext.CharacterId || existing.Content != content)
                {
                    _logger.LogWarning(
                        "[CharacterMemoryFeedbackService] Idempotency conflict for CharacterId={CharacterId}, ExecutionId={ExecutionId}. Existing content '{Existing}' != incoming content '{Incoming}'.",
                        cycleContext.CharacterId, cycleContext.ExecutionId, existing.Content, content);

                    throw new CharacterMemoryIdempotencyConflictException(
                        $"ExecutionId '{cycleContext.ExecutionId}' has already been processed with a different feedback payload.");
                }

                _logger.LogInformation(
                    "[CharacterMemoryFeedbackService] Memory feedback already exists for CharacterId={CharacterId}, ExecutionId={ExecutionId}. Idempotently reusing MemoryId={MemoryId}.",
                    cycleContext.CharacterId, cycleContext.ExecutionId, existing.Id);

                return new CharacterMemoryFeedback(
                    MemoryId: existing.Id,
                    CharacterId: existing.CharacterId,
                    CycleId: cycleContext.CycleId,
                    EventId: cycleContext.Event?.EventId,
                    ExecutionId: cycleContext.ExecutionId,
                    OccurredAtUtc: new DateTimeOffset(existing.CreatedAt, TimeSpan.Zero),
                    Type: feedbackType,
                    Content: existing.Content
                );
            }

            var memory = CharacterMemory.Create(
                characterId: cycleContext.CharacterId,
                userId: cycleContext.CharacterId,
                content: content,
                type: Domain.Enums.MemoryType.Event,
                importance: feedbackType == CharacterMemoryFeedbackType.ActionCompleted ? 3 : 2,
                confidence: 1.0m,
                sourceSessionId: cycleContext.ExecutionId
            );
            memory.Id = memoryId;

            await _dbContext.CharacterMemories.AddAsync(memory, ct);
            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[CharacterMemoryFeedbackService] Successfully persisted memory feedback MemoryId={MemoryId} for CharacterId={CharacterId}, CycleId={CycleId}.",
                memoryId, cycleContext.CharacterId, cycleContext.CycleId);

            return new CharacterMemoryFeedback(
                MemoryId: memoryId,
                CharacterId: cycleContext.CharacterId,
                CycleId: cycleContext.CycleId,
                EventId: cycleContext.Event?.EventId,
                ExecutionId: cycleContext.ExecutionId,
                OccurredAtUtc: cycleContext.TriggeredAtUtc,
                Type: feedbackType,
                Content: content
            );
        }
        catch (CharacterMemoryIdempotencyConflictException)
        {
            throw;
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex,
                "[CharacterMemoryFeedbackService] Concurrency race detected when saving memory feedback. Querying existing record for MemoryId={MemoryId}.",
                memoryId);

            var existing = await _dbContext.CharacterMemories
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == memoryId || (m.CharacterId == cycleContext.CharacterId && m.SourceSessionId == cycleContext.ExecutionId), ct);

            if (existing != null)
            {
                if (existing.CharacterId != cycleContext.CharacterId || existing.Content != content)
                {
                    throw new CharacterMemoryIdempotencyConflictException(
                        $"ExecutionId '{cycleContext.ExecutionId}' has already been processed with a different feedback payload.");
                }

                return new CharacterMemoryFeedback(
                    MemoryId: existing.Id,
                    CharacterId: existing.CharacterId,
                    CycleId: cycleContext.CycleId,
                    EventId: cycleContext.Event?.EventId,
                    ExecutionId: cycleContext.ExecutionId,
                    OccurredAtUtc: new DateTimeOffset(existing.CreatedAt, TimeSpan.Zero),
                    Type: feedbackType,
                    Content: existing.Content
                );
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[CharacterMemoryFeedbackService] Non-fatal error persisting memory feedback for CharacterId={CharacterId}, CycleId={CycleId}. Action state remains committed.",
                cycleContext.CharacterId, cycleContext.CycleId);

            return null;
        }
    }

    private static (CharacterMemoryFeedbackType Type, string Content) DetermineFeedback(CharacterCognitiveCycleResult result)
    {
        return result.Status switch
        {
            CharacterCognitiveCycleStatus.CompletedWithAction or CharacterCognitiveCycleStatus.AlreadyExecuted =>
                (CharacterMemoryFeedbackType.ActionCompleted,
                 $"Performed action {result.ActionProposal?.Proposal?.Type}: {result.ActionProposal?.Proposal?.Motivation}."),

            CharacterCognitiveCycleStatus.Failed or CharacterCognitiveCycleStatus.ConcurrencyConflict or CharacterCognitiveCycleStatus.IdempotencyConflict =>
                (CharacterMemoryFeedbackType.ActionFailed,
                 $"Attempted action {result.ActionProposal?.Proposal?.Type} but execution failed: {result.Message}."),

            CharacterCognitiveCycleStatus.CompletedWithoutAction when result.Event != null =>
                (CharacterMemoryFeedbackType.EventExperienced,
                 $"Experienced {result.Event.EventType} from {result.Event.Source}: {GetEventContent(result.Event)}."),

            _ =>
                (CharacterMemoryFeedbackType.NoActionTaken,
                 $"Completed cognitive cycle with no actionable proposal.")
        };
    }

    private static string GetEventContent(CharacterCognitiveEvent cognitiveEvent)
    {
        return cognitiveEvent switch
        {
            UserMessageCognitiveEvent userMsg => userMsg.Message,
            WorldCognitiveEvent worldEvt => worldEvt.EventName,
            _ => "Unknown event"
        };
    }

    private static Guid CreateDeterministicMemoryId(Guid characterId, Guid executionId)
    {
        var canonical = $"MemoryFeedback:{characterId:D}:{executionId:D}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }
}
