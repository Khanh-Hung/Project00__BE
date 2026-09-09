using System;
using System.Linq;
using System.Threading.Tasks;
using Application.Contracts.ActionExecution;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories.Core;
using Infrastructure.Services.SocialPresence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.GoalSystem;
using Xunit;

namespace Tests.SocialPresence;

public sealed class CharacterSocialPresenceRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 9, 10, 0, 0, TimeSpan.Zero);

    public CharacterSocialPresenceRepositoryTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new CoreDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task TryCreateAsync_And_GetByCharacterIdAsync_PersistsAndRetrieves()
    {
        var charId = Guid.NewGuid();
        using var db = new CoreDbContext(_options);
        var repo = new CharacterSocialPresenceRepository(db);

        var defaultPresence = CharacterSocialPresence.CreateDefault(charId, FixedNow);
        var (isCreated, created) = await repo.TryCreateAsync(defaultPresence);

        Assert.True(isCreated);
        Assert.NotNull(created);
        Assert.Equal(charId, created.CharacterId);
        Assert.Equal(SocialPresenceStatus.Active, created.Status);
        Assert.Equal(LifeActivityType.Idle, created.CurrentActivityType);

        var retrieved = await repo.GetByCharacterIdAsync(charId);
        Assert.NotNull(retrieved);
        Assert.Equal(created.Id, retrieved.Id);
    }

    [Fact]
    public async Task UpdateAsync_PersistsPropertyChangesAndVersionBump()
    {
        var charId = Guid.NewGuid();
        using (var db = new CoreDbContext(_options))
        {
            var repo = new CharacterSocialPresenceRepository(db);
            await repo.TryCreateAsync(CharacterSocialPresence.CreateDefault(charId, FixedNow));
        }

        var updateTime = FixedNow.AddMinutes(5);
        using (var db = new CoreDbContext(_options))
        {
            var repo = new CharacterSocialPresenceRepository(db);
            var presence = await repo.GetByCharacterIdAsync(charId);
            Assert.NotNull(presence);

            presence.UpdateActivity(LifeActivityType.Socialize, updateTime, RelationshipTargetType.User, Guid.NewGuid());
            await repo.UpdateAsync(presence);
        }

        using (var db = new CoreDbContext(_options))
        {
            var repo = new CharacterSocialPresenceRepository(db);
            var reloaded = await repo.GetByCharacterIdAsync(charId);
            Assert.NotNull(reloaded);
            Assert.Equal(LifeActivityType.Socialize, reloaded.CurrentActivityType);
            Assert.Equal(2u, reloaded.Version);
            Assert.Equal(updateTime, reloaded.UpdatedAtUtc);
        }
    }

    [Fact]
    public async Task ConcurrencyRace_DeterministicBarrier_EnforcesSingleAuthoritativeRow()
    {
        var charId = Guid.NewGuid();

        // Setup Worker B with SaveChangesInterceptor to deterministically suspend before save
        var interceptorB = new ConcurrencyBarrierInterceptor();
        var optionsB = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptorB)
            .Options;

        using var dbA = new CoreDbContext(_options);
        using var dbB = new CoreDbContext(optionsB);

        var repoA = new CharacterSocialPresenceRepository(dbA);
        var repoB = new CharacterSocialPresenceRepository(dbB);

        // 1. Worker B starts and pauses before SaveChangesAsync
        var candidateB = CharacterSocialPresence.CreateDefault(charId, FixedNow);
        var taskB = Task.Run(async () => await repoB.TryCreateAsync(candidateB));
        await interceptorB.WaitForBeforeSaveAsync();

        // 2. Worker A executes to completion (winner)
        var candidateA = CharacterSocialPresence.CreateDefault(charId, FixedNow);
        var (isCreatedA, presenceA) = await repoA.TryCreateAsync(candidateA);
        Assert.True(isCreatedA);
        Assert.NotNull(presenceA);

        // 3. Release Worker B: Worker B hits UNIQUE constraint, catches DbUpdateException,
        // detaches candidate, reloads authoritative winner from DB
        interceptorB.Release();
        var (isCreatedB, presenceB) = await taskB;

        Assert.False(isCreatedB); // Worker B detected conflict and reloaded winner
        Assert.NotNull(presenceB);
        Assert.Equal(presenceA.Id, presenceB.Id);

        // 4. Verify exactly 1 presence row exists in DB
        using var verifyDb = new CoreDbContext(_options);
        var rows = await verifyDb.CharacterSocialPresences.Where(p => p.CharacterId == charId).ToListAsync();
        Assert.Single(rows);
    }

    [Fact]
    public async Task AddTransitionAsync_And_GetTransitionAsync_PersistsAndRetrieves()
    {
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        var transition = new CharacterSocialPresenceTransition(
            characterId: charId,
            executionId: execId,
            actionType: "Socialize",
            oldStatus: SocialPresenceStatus.Active,
            newStatus: SocialPresenceStatus.Active,
            oldActivityType: LifeActivityType.Idle,
            newActivityType: LifeActivityType.Socialize,
            versionBefore: 1,
            versionAfter: 2,
            appliedAtUtc: FixedNow,
            targetType: RelationshipTargetType.User,
            targetId: targetId
        );

        using (var db = new CoreDbContext(_options))
        {
            var repo = new CharacterSocialPresenceRepository(db);
            await repo.AddTransitionAsync(transition);
        }

        using (var db = new CoreDbContext(_options))
        {
            var repo = new CharacterSocialPresenceRepository(db);
            var retrieved = await repo.GetTransitionAsync(charId, execId);

            Assert.NotNull(retrieved);
            Assert.Equal(execId, retrieved.ExecutionId);
            Assert.Equal("Socialize", retrieved.ActionType);
            Assert.Equal(LifeActivityType.Socialize, retrieved.NewActivityType);

            var recents = await repo.GetRecentTransitionsAsync(charId, 10);
            Assert.Single(recents);
        }
    }

    [Fact]
    public async Task TransitionService_IdempotencyAndDivergentReplay()
    {
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        using var db = new CoreDbContext(_options);
        var repo = new CharacterSocialPresenceRepository(db);
        var service = new SocialPresenceTransitionService(repo, NullLogger<SocialPresenceTransitionService>.Instance);

        var actionExec = new CharacterActionExecutionResult(
            ExecutionId: execId,
            CharacterId: charId,
            Status: CharacterActionExecutionStatus.Applied,
            ActionType: ActionType.Socialize,
            Intensity: 0.8,
            SourceIntent: IntentType.SeekSocialConnection,
            Motivation: MotivationType.ConnectionDriven,
            StateVersionBefore: 1,
            StateVersionAfter: 2,
            AppliedDelta: null
        );

        // 1. First application: inserts transition and updates presence
        var feedback1 = await service.ApplyActionExecutionFeedbackAsync(
            charId, execId, actionExec, FixedNow, RelationshipTargetType.User, targetId);

        Assert.NotNull(feedback1);
        Assert.Equal(LifeActivityType.Socialize, feedback1.CurrentActivityType);

        // 2. Duplicate replay with identical parameters: idempotent, returns existing feedback
        var feedback2 = await service.ApplyActionExecutionFeedbackAsync(
            charId, execId, actionExec, FixedNow, RelationshipTargetType.User, targetId);

        Assert.NotNull(feedback2);
        Assert.Equal(feedback1.CurrentActivityType, feedback2.CurrentActivityType);

        // Verify only 1 transition row in DB
        var transitions = await repo.GetRecentTransitionsAsync(charId, 10);
        Assert.Single(transitions);

        // 3. Divergent replay: same ExecutionId but different action type -> throws InvalidOperationException
        var divergentActionExec = new CharacterActionExecutionResult(
            ExecutionId: execId,
            CharacterId: charId,
            Status: CharacterActionExecutionStatus.Applied,
            ActionType: ActionType.Rest,
            Intensity: 0.5,
            SourceIntent: IntentType.SeekRest,
            Motivation: MotivationType.RestorationDriven,
            StateVersionBefore: 1,
            StateVersionAfter: 2,
            AppliedDelta: null
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyActionExecutionFeedbackAsync(
                charId, execId, divergentActionExec, FixedNow, RelationshipTargetType.User, targetId));
    }

    [Fact]
    public async Task AtomicRollback_SimulatedFailure_RollsBackPresenceAndTransition()
    {
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();

        // 1. Pre-seed initial presence: Status = Active, CurrentActivityType = Idle, Version = 1
        using (var setupDb = new CoreDbContext(_options))
        {
            var repo = new CharacterSocialPresenceRepository(setupDb);
            await repo.TryCreateAsync(CharacterSocialPresence.CreateDefault(charId, FixedNow));
        }

        // 2. Setup DbContext with throwing interceptor
        var interceptor = new ThrowingSaveChangesInterceptor();
        var throwingOptions = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;

        using (var db = new CoreDbContext(throwingOptions))
        {
            var repo = new CharacterSocialPresenceRepository(db);

            // Attempt transition: interceptor throws inside SavingChangesAsync
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repo.RecordTransitionAtomicAsync(
                    charId, execId, "Socialize", LifeActivityType.Socialize,
                    RelationshipTargetType.User, Guid.NewGuid(), FixedNow.AddMinutes(1)));
        }

        // 3. Verify in clean DbContext: Presence MUST be completely unmutated, Transition MUST NOT exist
        using (var verifyDb = new CoreDbContext(_options))
        {
            var presence = await verifyDb.CharacterSocialPresences.FirstOrDefaultAsync(p => p.CharacterId == charId);
            Assert.NotNull(presence);
            Assert.Equal(1u, presence.Version);
            Assert.Equal(SocialPresenceStatus.Active, presence.Status);
            Assert.Equal(LifeActivityType.Idle, presence.CurrentActivityType);

            var transitions = await verifyDb.CharacterSocialPresenceTransitions
                .Where(t => t.CharacterId == charId)
                .ToListAsync();
            Assert.Empty(transitions);
        }
    }

    [Fact]
    public async Task RetryAfterFailedTransaction_SucceedsCleanly()
    {
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        // 1. Initial presence
        using (var setupDb = new CoreDbContext(_options))
        {
            var repo = new CharacterSocialPresenceRepository(setupDb);
            await repo.TryCreateAsync(CharacterSocialPresence.CreateDefault(charId, FixedNow));
        }

        // 2. First attempt fails due to simulated crash
        var interceptor = new ThrowingSaveChangesInterceptor();
        var throwingOptions = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;

        using (var db = new CoreDbContext(throwingOptions))
        {
            var repo = new CharacterSocialPresenceRepository(db);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repo.RecordTransitionAtomicAsync(
                    charId, execId, "Socialize", LifeActivityType.Socialize,
                    RelationshipTargetType.User, targetId, FixedNow.AddMinutes(1)));
        }

        // 3. Retry with clean DbContext and identical parameters succeeds
        using (var retryDb = new CoreDbContext(_options))
        {
            var repo = new CharacterSocialPresenceRepository(retryDb);
            var (presence, transition, isDuplicate) = await repo.RecordTransitionAtomicAsync(
                charId, execId, "Socialize", LifeActivityType.Socialize,
                RelationshipTargetType.User, targetId, FixedNow.AddMinutes(1));

            Assert.False(isDuplicate);
            Assert.NotNull(presence);
            Assert.Equal(2u, presence.Version);
            Assert.Equal(LifeActivityType.Socialize, presence.CurrentActivityType);
            Assert.NotNull(transition);
            Assert.Equal(execId, transition.ExecutionId);
        }

        // 4. Verify DB state: exactly 1 transition, presence at version 2
        using (var verifyDb = new CoreDbContext(_options))
        {
            var presence = await verifyDb.CharacterSocialPresences.FirstAsync(p => p.CharacterId == charId);
            Assert.Equal(2u, presence.Version);
            Assert.Equal(LifeActivityType.Socialize, presence.CurrentActivityType);

            var transitions = await verifyDb.CharacterSocialPresenceTransitions
                .Where(t => t.CharacterId == charId)
                .ToListAsync();
            Assert.Single(transitions);
        }
    }

    [Fact]
    public async Task ConcurrentSameExecutionId_DeterministicBarrier_OnlyOneTransitionPersistedAndPresenceMutatedOnce()
    {
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        // Setup Worker B with SaveChangesInterceptor to deterministically suspend before save
        var interceptorB = new ConcurrencyBarrierInterceptor();
        var optionsB = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptorB)
            .Options;

        using var dbA = new CoreDbContext(_options);
        using var dbB = new CoreDbContext(optionsB);

        var repoA = new CharacterSocialPresenceRepository(dbA);
        var repoB = new CharacterSocialPresenceRepository(dbB);

        // 1. Worker B starts and pauses before SaveChangesAsync
        var taskB = Task.Run(async () => await repoB.RecordTransitionAtomicAsync(
            charId, execId, "Socialize", LifeActivityType.Socialize,
            RelationshipTargetType.User, targetId, FixedNow));
        await interceptorB.WaitForBeforeSaveAsync();

        // 2. Worker A executes to completion (winner)
        var (presenceA, transitionA, isDuplicateA) = await repoA.RecordTransitionAtomicAsync(
            charId, execId, "Socialize", LifeActivityType.Socialize,
            RelationshipTargetType.User, targetId, FixedNow);

        Assert.False(isDuplicateA);
        Assert.NotNull(presenceA);
        Assert.NotNull(transitionA);
        Assert.Equal(2u, presenceA.Version);

        // 3. Release Worker B: Worker B resumes, catches unique constraint on (CharacterId, ExecutionId),
        // rolls back transaction, clears tracker, reloads authoritative winner transition + presence
        interceptorB.Release();
        var (presenceB, transitionB, isDuplicateB) = await taskB;

        Assert.True(isDuplicateB);
        Assert.NotNull(presenceB);
        Assert.NotNull(transitionB);
        Assert.Equal(presenceA.Id, presenceB.Id);
        Assert.Equal(transitionA.Id, transitionB.Id);
        Assert.Equal(2u, presenceB.Version); // NEVER mutated a second time

        // 4. Verify DB state: exactly 1 presence, exactly 1 transition
        using var verifyDb = new CoreDbContext(_options);
        var presences = await verifyDb.CharacterSocialPresences.Where(p => p.CharacterId == charId).ToListAsync();
        Assert.Single(presences);
        Assert.Equal(2u, presences[0].Version);

        var transitions = await verifyDb.CharacterSocialPresenceTransitions.Where(t => t.CharacterId == charId).ToListAsync();
        Assert.Single(transitions);
    }

    [Fact]
    public async Task ConcurrentSameExecutionId_UninitializedPresence_WinnerCreatesBothAndLoserReloadsAuthoritative()
    {
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        // Neither Presence nor Transition exists in DB initially
        var interceptorB = new ConcurrencyBarrierInterceptor();
        var optionsB = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptorB)
            .Options;

        using var dbA = new CoreDbContext(_options);
        using var dbB = new CoreDbContext(optionsB);

        var repoA = new CharacterSocialPresenceRepository(dbA);
        var repoB = new CharacterSocialPresenceRepository(dbB);

        // 1. Worker B starts and pauses before SaveChangesAsync
        var taskB = Task.Run(async () => await repoB.RecordTransitionAtomicAsync(
            charId, execId, "Socialize", LifeActivityType.Socialize,
            RelationshipTargetType.User, targetId, FixedNow));
        await interceptorB.WaitForBeforeSaveAsync();

        // 2. Worker A executes to completion (creates default presence + transition)
        var (presenceA, transitionA, isDuplicateA) = await repoA.RecordTransitionAtomicAsync(
            charId, execId, "Socialize", LifeActivityType.Socialize,
            RelationshipTargetType.User, targetId, FixedNow);

        Assert.False(isDuplicateA);
        Assert.NotNull(presenceA);
        Assert.NotNull(transitionA);

        // 3. Worker B resumes: catches conflict, reloads winner
        interceptorB.Release();
        var (presenceB, transitionB, isDuplicateB) = await taskB;

        Assert.True(isDuplicateB);
        Assert.NotNull(presenceB);
        Assert.NotNull(transitionB);
        Assert.Equal(presenceA.Id, presenceB.Id);
        Assert.Equal(transitionA.Id, transitionB.Id);

        // 4. Verify exactly 1 presence and 1 transition in DB
        using var verifyDb = new CoreDbContext(_options);
        var presences = await verifyDb.CharacterSocialPresences.Where(p => p.CharacterId == charId).ToListAsync();
        Assert.Single(presences);

        var transitions = await verifyDb.CharacterSocialPresenceTransitions.Where(t => t.CharacterId == charId).ToListAsync();
        Assert.Single(transitions);
    }

    [Fact]
    public async Task ConcurrentSameExecutionId_DivergentPayload_LoserDetectsDivergentFingerprintAndThrows()
    {
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        var interceptorB = new ConcurrencyBarrierInterceptor();
        var optionsB = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptorB)
            .Options;

        using var dbA = new CoreDbContext(_options);
        using var dbB = new CoreDbContext(optionsB);

        var repoA = new CharacterSocialPresenceRepository(dbA);
        var repoB = new CharacterSocialPresenceRepository(dbB);

        // 1. Worker B starts with "Rest" action payload and pauses
        var taskB = Task.Run(async () => await repoB.RecordTransitionAtomicAsync(
            charId, execId, "Rest", LifeActivityType.Rest,
            RelationshipTargetType.User, targetId, FixedNow));
        await interceptorB.WaitForBeforeSaveAsync();

        // 2. Worker A executes to completion with "Socialize" action payload
        var (presenceA, transitionA, isDuplicateA) = await repoA.RecordTransitionAtomicAsync(
            charId, execId, "Socialize", LifeActivityType.Socialize,
            RelationshipTargetType.User, targetId, FixedNow);
        Assert.False(isDuplicateA);

        // 3. Worker B resumes, catches DB conflict, reloads winner, detects divergent fingerprint, and throws InvalidOperationException
        interceptorB.Release();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => taskB);
        Assert.Contains("Divergent semantic replay", ex.Message);
    }

    private sealed class ThrowingSaveChangesInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public bool ShouldThrow { get; set; } = true;

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (ShouldThrow)
            {
                throw new InvalidOperationException("Simulated crash right before commit in SavingChangesAsync");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
