using Application.Common;
using Application.Enums;
using Application.Interfaces;
using Domain.Common;
using Domain.Entities;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.State;

public sealed class CharacterStateTransitionService : ICharacterStateTransitionService, ICharacterStateTransitionStager
{
    private readonly CoreDbContext _dbContext;
    private readonly ILogger<CharacterStateTransitionService> _logger;

    public CharacterStateTransitionService(
        CoreDbContext dbContext,
        ILogger<CharacterStateTransitionService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }


    public StateTransitionResult StageTransition(
        CharacterState state,
        CharacterStateDelta delta,
        StateTransitionContext context,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state, nameof(state));
        ArgumentNullException.ThrowIfNull(delta, nameof(delta));
        ArgumentNullException.ThrowIfNull(context, nameof(context));

        if (context.ExecutionId == Guid.Empty)
            throw new ArgumentException("ExecutionId cannot be empty.", nameof(context));

        if (context.ExpectedStateVersion.HasValue && state.Version != context.ExpectedStateVersion.Value)
        {
            return StateTransitionResult.ConcurrencyConflict(
                state.Version,
                $"Expected state version {context.ExpectedStateVersion.Value}, but authoritative state is at version {state.Version}.");
        }

        int versionBefore = state.Version;
        state.ApplyDelta(delta);
        int versionAfter = state.Version;

        var transition = new CharacterStateTransition(
            characterId: state.CharacterId,
            executionId: context.ExecutionId,
            sourceType: context.SourceType,
            sourceId: context.SourceId,
            delta: delta,
            versionBefore: versionBefore,
            versionAfter: versionAfter,
            appliedAtUtc: nowUtc
        );

        _dbContext.CharacterStateTransitions.Add(transition);

        return StateTransitionResult.Applied(state.ToSnapshot(), versionBefore, versionAfter);
    }

    public async Task<StateTransitionResult> TransitionAsync(
        Guid characterId,
        CharacterStateDelta delta,
        StateTransitionContext context,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delta, nameof(delta));
        ArgumentNullException.ThrowIfNull(context, nameof(context));

        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));
        if (context.ExecutionId == Guid.Empty)
            throw new ArgumentException("ExecutionId cannot be empty.", nameof(context));

        var expectedFingerprint = CanonicalTransitionFingerprint.Compute(
            characterId, context.ExecutionId, context.SourceType, context.SourceId, delta);

        // 1. Idempotency Check: Query transition ledger
        var existingTransition = await _dbContext.CharacterStateTransitions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.CharacterId == characterId && t.ExecutionId == context.ExecutionId, ct);

        if (existingTransition != null)
        {
            // Verify payload consistency (P1-1)
            if (existingTransition.TransitionFingerprint != expectedFingerprint)
            {
                _logger.LogWarning(
                    "[CharacterStateTransitionService] Idempotency conflict for CharacterId={CharacterId}, ExecutionId={ExecutionId}. Existing fingerprint '{Existing}' != incoming '{Incoming}'",
                    characterId, context.ExecutionId, existingTransition.TransitionFingerprint, expectedFingerprint);

                return StateTransitionResult.IdempotencyConflict(
                    $"ExecutionId '{context.ExecutionId}' has already been processed with a different payload.");
            }

            if (context.ExpectedStateVersion.HasValue && existingTransition.VersionBefore != context.ExpectedStateVersion.Value)
            {
                _logger.LogWarning(
                    "[CharacterStateTransitionService] State version mismatch for existing transition CharacterId={CharacterId}, ExecutionId={ExecutionId}. Recorded VersionBefore={RecordedVersion}, ExpectedVersion={ExpectedVersion}",
                    characterId, context.ExecutionId, existingTransition.VersionBefore, context.ExpectedStateVersion.Value);

                return StateTransitionResult.ConcurrencyConflict(
                    existingTransition.VersionBefore,
                    $"ExecutionId '{context.ExecutionId}' was recorded at state version {existingTransition.VersionBefore}, but incoming context expected version {context.ExpectedStateVersion.Value}.");
            }

            _logger.LogInformation(
                "[CharacterStateTransitionService] Idempotent duplicate transition suppressed for CharacterId={CharacterId}, ExecutionId={ExecutionId}, SourceType={SourceType}",
                characterId, context.ExecutionId, context.SourceType);

            var currentState = await _dbContext.CharacterStates
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.CharacterId == characterId, ct);

            if (currentState == null)
            {
                return StateTransitionResult.NotFound(
                    $"Authoritative character state for CharacterId {characterId} not found.");
            }

            return StateTransitionResult.AlreadyApplied(
                currentState.ToSnapshot(),
                existingTransition.VersionBefore,
                existingTransition.VersionAfter);
        }

        // 2. Load authoritative CharacterState (Strict: No fail-open CreateDefault!)
        var state = await _dbContext.CharacterStates
            .FirstOrDefaultAsync(s => s.CharacterId == characterId, ct);

        if (state == null)
        {
            _logger.LogError(
                "[CharacterStateTransitionService] Authoritative CharacterState not found for CharacterId={CharacterId}. Refusing to fail-open.",
                characterId);

            return StateTransitionResult.NotFound(
                $"Authoritative character state for CharacterId {characterId} does not exist. Explicit initialization required.");
        }

        if (context.ExpectedStateVersion.HasValue && state.Version != context.ExpectedStateVersion.Value)
        {
            _logger.LogWarning(
                "[CharacterStateTransitionService] State version mismatch for CharacterId={CharacterId}, ExecutionId={ExecutionId}. Authoritative Version={CurrentVersion}, ExpectedVersion={ExpectedVersion}",
                characterId, context.ExecutionId, state.Version, context.ExpectedStateVersion.Value);

            return StateTransitionResult.ConcurrencyConflict(
                state.Version,
                $"Expected state version {context.ExpectedStateVersion.Value}, but authoritative state is at version {state.Version}.");
        }

        int versionBefore = state.Version;
        StageTransition(state, delta, context, nowUtc);
        int versionAfter = state.Version;

        var isOuterTx = _dbContext.Database.CurrentTransaction != null;
        await using var tx = isOuterTx ? null : await _dbContext.Database.BeginTransactionAsync(ct);

        try
        {
            await _dbContext.SaveChangesAsync(ct);
            if (tx != null)
            {
                await tx.CommitAsync(ct);
            }

            _logger.LogInformation(
                "[CharacterStateTransitionService] State transition applied successfully for CharacterId={CharacterId}, ExecutionId={ExecutionId}, SourceType={SourceType}, Version={VersionBefore}->{VersionAfter}",
                characterId, context.ExecutionId, context.SourceType, versionBefore, versionAfter);

            return StateTransitionResult.Applied(state.ToSnapshot(), versionBefore, versionAfter);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            if (tx != null) await tx.RollbackAsync(ct);

            // Detach local entities before querying DB
            _dbContext.ChangeTracker.Clear();

            _logger.LogInformation(
                "[CharacterStateTransitionService] Database unique constraint caught duplicate transition for CharacterId={CharacterId}, ExecutionId={ExecutionId}",
                characterId, context.ExecutionId);

            var reloadedTransition = await _dbContext.CharacterStateTransitions
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.CharacterId == characterId && t.ExecutionId == context.ExecutionId, ct);

            if (reloadedTransition != null && reloadedTransition.TransitionFingerprint != expectedFingerprint)
            {
                return StateTransitionResult.IdempotencyConflict(
                    $"ExecutionId '{context.ExecutionId}' already committed with different payload.");
            }

            if (reloadedTransition != null && context.ExpectedStateVersion.HasValue && reloadedTransition.VersionBefore != context.ExpectedStateVersion.Value)
            {
                return StateTransitionResult.ConcurrencyConflict(
                    reloadedTransition.VersionBefore,
                    $"ExecutionId '{context.ExecutionId}' was recorded at state version {reloadedTransition.VersionBefore}, but incoming context expected version {context.ExpectedStateVersion.Value}.");
            }

            var reloadedState = await _dbContext.CharacterStates
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.CharacterId == characterId, ct);

            if (reloadedState == null)
            {
                return StateTransitionResult.NotFound(
                    $"Authoritative character state for CharacterId {characterId} not found after conflict.");
            }

            return StateTransitionResult.AlreadyApplied(
                reloadedState.ToSnapshot(),
                reloadedTransition?.VersionBefore ?? reloadedState.Version,
                reloadedTransition?.VersionAfter ?? reloadedState.Version);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            if (tx != null) await tx.RollbackAsync(ct);

            _logger.LogWarning(
                ex,
                "[CharacterStateTransitionService] Optimistic concurrency conflict during transition for CharacterId={CharacterId}, ExpectedVersion={Version}",
                characterId, versionBefore);

            _dbContext.ChangeTracker.Clear();

            return StateTransitionResult.ConcurrencyConflict(
                versionBefore,
                "Optimistic concurrency conflict occurred while updating character state.");
        }
        catch
        {
            if (tx != null) await tx.RollbackAsync(ct);
            _dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    public static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        return DbConstraintClassifier.IsUniqueViolation(
            ex,
            expectedPostgresConstraints: ["IX_CharacterStateTransitions_CharacterId_ExecutionId"],
            expectedSqliteTable: "CharacterStateTransitions",
            expectedSqliteColumns: ["CharacterId", "ExecutionId"]);
    }
}
