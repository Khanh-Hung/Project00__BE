using System.Security.Cryptography;
using System.Text;
using Application.Common;
using Application.Enums;
using Application.Interfaces;
using Domain.Entities;
using Domain.Policies;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.State;

public sealed class CharacterStateService : ICharacterStateService
{
    private readonly CoreDbContext _dbContext;
    private readonly ICharacterStateTransitionService _transitionService;
    private readonly ICharacterStateEvolutionPolicy _evolutionPolicy;
    private readonly ILogger<CharacterStateService> _logger;

    public CharacterStateService(
        CoreDbContext dbContext,
        ICharacterStateTransitionService transitionService,
        ICharacterStateEvolutionPolicy evolutionPolicy,
        ILogger<CharacterStateService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _transitionService = transitionService ?? throw new ArgumentNullException(nameof(transitionService));
        _evolutionPolicy = evolutionPolicy ?? throw new ArgumentNullException(nameof(evolutionPolicy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CharacterStateSnapshot?> GetAsync(
        Guid characterId,
        CancellationToken ct = default)
    {
        if (characterId == Guid.Empty) return null;

        var state = await _dbContext.CharacterStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CharacterId == characterId, ct);

        return state?.ToSnapshot();
    }

    public async Task<CharacterStateSnapshot> GetOrCreateInitialStateAsync(
        Guid characterId,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        var existing = await _dbContext.CharacterStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CharacterId == characterId, ct);

        if (existing != null)
        {
            return existing.ToSnapshot();
        }

        var newState = CharacterState.CreateDefault(characterId, nowUtc);
        try
        {
            await _dbContext.CharacterStates.AddAsync(newState, ct);
            await _dbContext.SaveChangesAsync(ct);
            return newState.ToSnapshot();
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            _dbContext.Entry(newState).State = EntityState.Detached;

            var winner = await _dbContext.CharacterStates
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.CharacterId == characterId, ct);

            return winner?.ToSnapshot() ?? newState.ToSnapshot();
        }
    }

    public Task<StateTransitionResult> ApplyDeltaAsync(
        Guid characterId,
        CharacterStateDelta delta,
        StateTransitionContext context,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        return _transitionService.TransitionAsync(characterId, delta, context, nowUtc, ct);
    }

    public async Task<StateTransitionResult> EvolveToAsync(
        Guid characterId,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        const int maxConcurrencyRetries = 3;
        for (int attempt = 1; attempt <= maxConcurrencyRetries; attempt++)
        {
            var state = await _dbContext.CharacterStates
                .FirstOrDefaultAsync(s => s.CharacterId == characterId, ct);

            if (state == null)
            {
                await GetOrCreateInitialStateAsync(characterId, nowUtc, ct);
                state = await _dbContext.CharacterStates
                    .FirstOrDefaultAsync(s => s.CharacterId == characterId, ct);
            }

            if (state == null)
            {
                return StateTransitionResult.NotFound($"Character state for CharacterId {characterId} could not be loaded or initialized.");
            }

            // Invariant: LastEvolvedAtUtc never regresses
            if (nowUtc <= state.LastEvolvedAtUtc)
            {
                _logger.LogInformation(
                    "[CharacterStateService] State already evolved up to or past {NowUtc:O} (LastEvolvedAtUtc={LastEvolvedAt:O}) for CharacterId={CharacterId}",
                    nowUtc, state.LastEvolvedAtUtc, characterId);

                return StateTransitionResult.AlreadyApplied(state.ToSnapshot(), state.Version);
            }

            var delta = _evolutionPolicy.CalculateEvolutionDelta(state.ToSnapshot(), state.LastEvolvedAtUtc, nowUtc);

            int versionBefore = state.Version;
            state.Evolve(delta, nowUtc);
            int versionAfter = state.Version;

            var evolutionExecutionId = GenerateEvolutionExecutionId(characterId, nowUtc);
            var transition = new CharacterStateTransition(
                characterId: characterId,
                executionId: evolutionExecutionId,
                sourceType: "TemporalEvolution",
                sourceId: nowUtc.ToString("O"),
                delta: delta,
                versionBefore: versionBefore,
                versionAfter: versionAfter,
                appliedAtUtc: nowUtc
            );

            try
            {
                await _dbContext.CharacterStateTransitions.AddAsync(transition, ct);
                await _dbContext.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "[CharacterStateService] Successfully evolved CharacterId={CharacterId} to {NowUtc:O} (Version {VersionBefore}->{VersionAfter})",
                    characterId, nowUtc, versionBefore, versionAfter);

                return StateTransitionResult.Applied(state.ToSnapshot(), versionBefore, versionAfter);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // Unique violation on (CharacterId, EvolutionExecutionId): Another worker already completed this evolution
                _dbContext.Entry(transition).State = EntityState.Detached;
                _dbContext.Entry(state).State = EntityState.Detached;

                var current = await _dbContext.CharacterStates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.CharacterId == characterId, ct);

                return StateTransitionResult.AlreadyApplied(
                    current?.ToSnapshot() ?? state.ToSnapshot(),
                    current?.Version ?? versionBefore);
            }
            catch (DbUpdateConcurrencyException)
            {
                _dbContext.Entry(transition).State = EntityState.Detached;
                _dbContext.Entry(state).State = EntityState.Detached;

                // Reload state to see if concurrent winner already evolved to nowUtc or beyond
                var refreshed = await _dbContext.CharacterStates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.CharacterId == characterId, ct);

                if (refreshed != null && refreshed.LastEvolvedAtUtc >= nowUtc)
                {
                    _logger.LogInformation(
                        "[CharacterStateService] Concurrent evolution won by another worker for CharacterId={CharacterId}, current LastEvolvedAtUtc={LastEvolvedAt:O}",
                        characterId, refreshed.LastEvolvedAtUtc);

                    return StateTransitionResult.AlreadyApplied(refreshed.ToSnapshot(), refreshed.Version);
                }

                // If not yet evolved to nowUtc and retries remaining, retry next loop
                if (attempt == maxConcurrencyRetries)
                {
                    return StateTransitionResult.ConcurrencyConflict(
                        versionBefore,
                        $"Concurrency conflict during evolution after {maxConcurrencyRetries} attempts.");
                }
            }
        }

        return StateTransitionResult.ConcurrencyConflict(0, "Exceeded concurrency retry limit during evolution.");
    }

    private static Guid GenerateEvolutionExecutionId(Guid characterId, DateTime nowUtc)
    {
        var raw = $"{characterId:D}:Evolve:{nowUtc.Ticks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);
        return new Guid(guidBytes);
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        return DbConstraintClassifier.IsUniqueViolation(
            ex,
            expectedPostgresConstraints: ["IX_CharacterStates_CharacterId"],
            expectedSqliteTable: "CharacterStates",
            expectedSqliteColumns: ["CharacterId"]);
    }
}
