using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Application.Contracts.ActionExecution;
using Application.Contracts.CognitiveCycle;
using Application.Interfaces;
using Domain.Common;
using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories;
using Infrastructure.Services.ActionExecution;
using Infrastructure.Services.CognitiveCycle;
using Infrastructure.Services.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.CognitiveCycle;

public sealed class CharacterPersonalityAdaptationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public CharacterPersonalityAdaptationTests()
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

    #region Helper Setup Methods

    private async Task<Guid> SeedCharacterStateAsync(
        decimal hunger = 80m,
        decimal energy = 80m,
        decimal stress = 20m,
        decimal socialNeed = 50m,
        decimal comfort = 50m)
    {
        var charId = Guid.NewGuid();
        await using var db = new CoreDbContext(_options);
        var state = new CharacterState(
            charId,
            initializedAtUtc: DateTime.UtcNow,
            hunger: hunger,
            energy: energy,
            stress: stress,
            socialNeed: socialNeed,
            comfort: comfort
        );
        db.CharacterStates.Add(state);
        await db.SaveChangesAsync();
        return charId;
    }

    private CharacterCognitiveCycleService CreateService(
        CoreDbContext db,
        IPersonalityAdaptationService? personalityAdaptationService = null,
        ICharacterActionExecutionService? actionExecutionService = null,
        ICharacterRelationshipFeedbackService? relationshipFeedbackService = null)
    {
        var transitionService = new CharacterStateTransitionService(
            db,
            NullLogger<CharacterStateTransitionService>.Instance);

        var stateEvolutionPolicy = new CharacterStateEvolutionPolicy();
        var stateService = new CharacterStateService(
            db,
            transitionService,
            stateEvolutionPolicy,
            NullLogger<CharacterStateService>.Instance);

        var execPolicy = new CharacterActionExecutionPolicy();
        var execService = actionExecutionService ?? new CharacterActionExecutionService(
            transitionService,
            execPolicy,
            NullLogger<CharacterActionExecutionService>.Instance);

        var memoryRetrieval = new CharacterMemoryRetrievalService(
            db,
            NullLogger<CharacterMemoryRetrievalService>.Instance);

        var memoryFeedback = new CharacterMemoryFeedbackService(
            db,
            NullLogger<CharacterMemoryFeedbackService>.Instance);

        var relRepo = new CharacterRelationshipRepository(db);
        var relRetrieval = new CharacterRelationshipRetrievalService(
            relRepo,
            NullLogger<CharacterRelationshipRetrievalService>.Instance);

        var relTransition = new CharacterRelationshipTransitionService(
            db,
            NullLogger<CharacterRelationshipTransitionService>.Instance);

        var relFeedback = relationshipFeedbackService ?? new CharacterRelationshipFeedbackService(
            relTransition,
            new DefaultCharacterRelationshipFeedbackPolicy(),
            NullLogger<CharacterRelationshipFeedbackService>.Instance);

        var personalityService = personalityAdaptationService ?? new PersonalityAdaptationService(
            db,
            new DefaultPersonalityAdaptationPolicy(),
            NullLogger<PersonalityAdaptationService>.Instance,
            threshold: 3);

        return new CharacterCognitiveCycleService(
            stateService: stateService,
            experiencePolicy: new CharacterInternalExperiencePolicy(),
            appraisalPolicy: new CharacterAppraisalPolicy(),
            emotionPolicy: new CharacterEmotionPolicy(),
            desirePolicy: new CharacterDesirePolicy(),
            intentPolicy: new CharacterIntentPolicy(),
            actionProposalPolicy: new CharacterActionProposalPolicy(),
            actionExecutionService: execService,
            logger: NullLogger<CharacterCognitiveCycleService>.Instance,
            memoryRetrievalService: memoryRetrieval,
            memoryFeedbackService: memoryFeedback,
            relationshipRetrievalService: relRetrieval,
            relationshipFeedbackService: relFeedback,
            personalityAdaptationService: personalityService
        );
    }

    #endregion

    #region 1. Domain Tests

    [Fact]
    public void CharacterPersonality_InitializesWithDefaultValues()
    {
        var charId = Guid.NewGuid();
        var personality = CharacterPersonality.CreateDefault(charId);

        Assert.Equal(charId, personality.CharacterId);
        Assert.Equal(50, personality.Warmth);
        Assert.Equal(50, personality.Openness);
        Assert.Equal(50, personality.Assertiveness);
        Assert.Equal(50, personality.Conscientiousness);
        Assert.Equal(50, personality.SocialConfidence);
        Assert.Equal(50, personality.TrustDisposition);
        Assert.Equal(50, personality.EmotionalStability);
        Assert.Equal(1u, personality.Version);
    }

    [Fact]
    public void CharacterPersonality_GetTrait_RejectsInvalidTraitKey()
    {
        var personality = CharacterPersonality.CreateDefault(Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => personality.GetTrait("InvalidTrait"));
        Assert.Throws<ArgumentException>(() => personality.GetTrait(""));
    }

    [Fact]
    public void CharacterPersonality_AdaptTrait_AppliesPositiveAndNegativeStep()
    {
        var personality = CharacterPersonality.CreateDefault(Guid.NewGuid());

        var (before1, after1) = personality.AdaptTrait(PersonalityTraitKeys.Warmth, +1);
        Assert.Equal(50, before1);
        Assert.Equal(51, after1);
        Assert.Equal(51, personality.Warmth);
        Assert.Equal(2u, personality.Version);

        var (before2, after2) = personality.AdaptTrait(PersonalityTraitKeys.Warmth, -1);
        Assert.Equal(51, before2);
        Assert.Equal(50, after2);
        Assert.Equal(50, personality.Warmth);
        Assert.Equal(3u, personality.Version);
    }

    [Fact]
    public void CharacterPersonality_AdaptTrait_ClampsAtMinAndMaxBoundaries()
    {
        var personalityMax = new CharacterPersonality(
            Guid.NewGuid(),
            warmth: 100,
            emotionalStability: 0
        );

        // Attempting to exceed 100 clamps to 100
        var (beforeMax, afterMax) = personalityMax.AdaptTrait(PersonalityTraitKeys.Warmth, +1);
        Assert.Equal(100, beforeMax);
        Assert.Equal(100, afterMax);
        Assert.Equal(100, personalityMax.Warmth);

        // Attempting to go below 0 clamps to 0
        var (beforeMin, afterMin) = personalityMax.AdaptTrait(PersonalityTraitKeys.EmotionalStability, -1);
        Assert.Equal(0, beforeMin);
        Assert.Equal(0, afterMin);
        Assert.Equal(0, personalityMax.EmotionalStability);
    }

    [Fact]
    public void CharacterPersonality_AdaptTrait_RejectsLargeRequestedDelta()
    {
        var personality = CharacterPersonality.CreateDefault(Guid.NewGuid());

        // Single gradual adaptation cannot exceed +/-1 point
        Assert.Throws<ArgumentOutOfRangeException>(() => personality.AdaptTrait(PersonalityTraitKeys.Warmth, +2));
        Assert.Throws<ArgumentOutOfRangeException>(() => personality.AdaptTrait(PersonalityTraitKeys.Warmth, -5));
    }

    [Fact]
    public void CharacterPersonality_AdaptTrait_ZeroDelta_ReturnsCurrentWithoutVersionBump()
    {
        var personality = CharacterPersonality.CreateDefault(Guid.NewGuid());

        var (before, after) = personality.AdaptTrait(PersonalityTraitKeys.Warmth, 0);
        Assert.Equal(50, before);
        Assert.Equal(50, after);
        Assert.Equal(1u, personality.Version);
    }

    [Fact]
    public void PersonalityTraitKeys_NormalizesCaseInsensitive()
    {
        Assert.True(PersonalityTraitKeys.IsValid("warmth"));
        Assert.Equal(PersonalityTraitKeys.Warmth, PersonalityTraitKeys.Normalize("warmth"));
        Assert.Equal(PersonalityTraitKeys.EmotionalStability, PersonalityTraitKeys.Normalize("EMOTIONALSTABILITY"));
        Assert.False(PersonalityTraitKeys.IsValid("Unknown"));
    }

    #endregion

    #region 2. Evidence & Fingerprint Tests

    [Fact]
    public void CanonicalPersonalityFingerprint_IsDeterministic()
    {
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();

        var hash1 = CanonicalPersonalityFingerprint.ComputeEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1, "Positive interaction");

        var hash2 = CanonicalPersonalityFingerprint.ComputeEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1, "Positive interaction");

        Assert.Equal(hash1, hash2);
        Assert.False(string.IsNullOrWhiteSpace(hash1));
        Assert.Equal(64, hash1.Length);
    }

    [Fact]
    public void CanonicalPersonalityFingerprint_DifferentPayloads_ProduceDifferentFingerprints()
    {
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();

        var hash1 = CanonicalPersonalityFingerprint.ComputeEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1, "Reason A");

        var hash2 = CanonicalPersonalityFingerprint.ComputeEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.NegativeSocialOutcome,
            PersonalityTraitKeys.Warmth, -1, 1, "Reason B");

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void PersonalityAdaptationEvidence_RejectsInvalidArguments()
    {
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => new PersonalityAdaptationEvidence(
            Guid.Empty, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1, "Reason", "fingerprint"));

        Assert.Throws<ArgumentException>(() => new PersonalityAdaptationEvidence(
            charId, Guid.Empty, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1, "Reason", "fingerprint"));

        Assert.Throws<ArgumentOutOfRangeException>(() => new PersonalityAdaptationEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, 0, 1, "Reason", "fingerprint"));

        Assert.Throws<ArgumentOutOfRangeException>(() => new PersonalityAdaptationEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 0, "Reason", "fingerprint"));
    }

    #endregion

    #region 3. Policy Tests

    [Fact]
    public void Policy_CompletedWithAction_PositiveRelationshipFeedback_ProducesPositiveSocialOutcomeEvidence()
    {
        var policy = new DefaultPersonalityAdaptationPolicy();
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();

        var context = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: execId,
            CharacterId: charId,
            TriggeredAtUtc: DateTimeOffset.UtcNow
        );

        var result = CreateSuccessResult(context, trustDelta: 3);

        var proposal = policy.Evaluate(context, result);

        Assert.NotNull(proposal);
        Assert.Equal(PersonalityAdaptationEvidenceType.PositiveSocialOutcome, proposal.EvidenceType);
        Assert.Equal(PersonalityTraitKeys.Warmth, proposal.TraitKey);
        Assert.Equal(+1, proposal.Direction);
        Assert.Equal(1, proposal.Strength);
    }

    [Fact]
    public void Policy_CompletedWithAction_NegativeRelationshipFeedback_ProducesNegativeSocialOutcomeEvidence()
    {
        var policy = new DefaultPersonalityAdaptationPolicy();
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();

        var context = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: execId,
            CharacterId: charId,
            TriggeredAtUtc: DateTimeOffset.UtcNow
        );

        var result = CreateSuccessResult(context, trustDelta: -3);

        var proposal = policy.Evaluate(context, result);

        Assert.NotNull(proposal);
        Assert.Equal(PersonalityAdaptationEvidenceType.NegativeSocialOutcome, proposal.EvidenceType);
        Assert.Equal(PersonalityTraitKeys.Warmth, proposal.TraitKey);
        Assert.Equal(-1, proposal.Direction);
    }

    [Fact]
    public void Policy_InfrastructureFailures_ProduceZeroEvidence()
    {
        var policy = new DefaultPersonalityAdaptationPolicy();
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();

        var context = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: execId,
            CharacterId: charId,
            TriggeredAtUtc: DateTimeOffset.UtcNow
        );

        var conflict = CharacterCognitiveCycleResult.ConcurrencyConflict(
            context.CycleId, execId, charId, context.TriggeredAtUtc, 1);
        Assert.Null(policy.Evaluate(context, conflict));

        var failed = CharacterCognitiveCycleResult.Failed(
            context.CycleId, execId, charId, context.TriggeredAtUtc, 1);
        Assert.Null(policy.Evaluate(context, failed));

        var invalidInput = CharacterCognitiveCycleResult.InvalidInput(
            context.CycleId, execId, charId, context.TriggeredAtUtc, "Invalid");
        Assert.Null(policy.Evaluate(context, invalidInput));

        var notFound = CharacterCognitiveCycleResult.NotFound(
            context.CycleId, execId, charId, context.TriggeredAtUtc, "NotFound");
        Assert.Null(policy.Evaluate(context, notFound));

        var withoutAction = CharacterCognitiveCycleResult.CompletedWithoutAction(
            context.CycleId, execId, charId, context.TriggeredAtUtc, 1);
        Assert.Null(policy.Evaluate(context, withoutAction));
    }

    #endregion

    #region 4. Idempotency & Conflict Tests

    [Fact]
    public async Task ProcessAdaptationAsync_WhenReplayedWithIdenticalPayload_IsIdempotentAndSuppressed()
    {
        await using var db = new CoreDbContext(_options);
        var charId = await SeedCharacterStateAsync();
        var execId = Guid.NewGuid();

        var service = new PersonalityAdaptationService(
            db, new DefaultPersonalityAdaptationPolicy(), NullLogger<PersonalityAdaptationService>.Instance, threshold: 3);

        var context = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: execId,
            CharacterId: charId,
            TriggeredAtUtc: DateTimeOffset.UtcNow
        );

        var result = CreateSuccessResult(context);

        // 1st Execution
        var res1 = await service.ProcessAdaptationAsync(context, result);
        Assert.NotNull(res1);

        // 2nd Execution with identical payload (idempotent replay)
        var res2 = await service.ProcessAdaptationAsync(context, result);
        Assert.NotNull(res2);
        Assert.Equal(res1.EvidenceId, res2.EvidenceId);

        // Exactly 1 evidence record in DB
        var totalEvidence = await db.CharacterPersonalityAdaptationEvidences.CountAsync(e => e.CharacterId == charId);
        Assert.Equal(1, totalEvidence);
    }

    [Fact]
    public async Task ProcessAdaptationAsync_WhenReplayedWithDifferentSemanticPayload_ThrowsIdempotencyConflict()
    {
        await using var db = new CoreDbContext(_options);
        var charId = await SeedCharacterStateAsync();
        var execId = Guid.NewGuid();

        var service = new PersonalityAdaptationService(
            db, new DefaultPersonalityAdaptationPolicy(), NullLogger<PersonalityAdaptationService>.Instance, threshold: 3);

        var context = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: execId,
            CharacterId: charId,
            TriggeredAtUtc: DateTimeOffset.UtcNow
        );

        // 1st execution: Positive relationship feedback
        var result1 = CreateSuccessResult(context, trustDelta: 3);
        var res1 = await service.ProcessAdaptationAsync(context, result1);
        Assert.NotNull(res1);

        // 2nd execution: Same ExecutionId, but CONFLICTING semantic payload (Negative relationship feedback)
        var result2 = CreateSuccessResult(context, trustDelta: -3);

        await Assert.ThrowsAsync<PersonalityAdaptationIdempotencyConflictException>(() =>
            service.ProcessAdaptationAsync(context, result2));
    }

    #endregion

    #region 5. Aggregation & Threshold Tests

    [Fact]
    public async Task ProcessAdaptationAsync_EvidenceBelowThreshold_DoesNotMutatePersonality()
    {
        await using var db = new CoreDbContext(_options);
        var charId = await SeedCharacterStateAsync();
        var service = new PersonalityAdaptationService(
            db, new DefaultPersonalityAdaptationPolicy(), NullLogger<PersonalityAdaptationService>.Instance, threshold: 3);

        // Run 2 successful interactions (threshold is 3)
        for (int i = 0; i < 2; i++)
        {
            var context = new CharacterCognitiveCycleContext(
                CycleId: Guid.NewGuid(),
                ExecutionId: Guid.NewGuid(),
                CharacterId: charId,
                TriggeredAtUtc: DateTimeOffset.UtcNow
            );
            var result = CreateSuccessResult(context, trustDelta: 2);
            var adaptationResult = await service.ProcessAdaptationAsync(context, result);

            Assert.NotNull(adaptationResult);
            Assert.False(adaptationResult.AdaptationTriggered);
        }

        // Personality should either be null or still at default (50) with 0 adaptations
        var adaptationsCount = await db.CharacterPersonalityAdaptations.CountAsync(a => a.CharacterId == charId);
        Assert.Equal(0, adaptationsCount);

        var personality = await db.CharacterPersonalities.FirstOrDefaultAsync(p => p.CharacterId == charId);
        if (personality != null)
        {
            Assert.Equal(50, personality.Warmth);
        }
    }

    [Fact]
    public async Task ProcessAdaptationAsync_WhenThresholdReached_MutatesPersonalityAndRecordsAdaptation()
    {
        await using var db = new CoreDbContext(_options);
        var charId = await SeedCharacterStateAsync();
        var service = new PersonalityAdaptationService(
            db, new DefaultPersonalityAdaptationPolicy(), NullLogger<PersonalityAdaptationService>.Instance, threshold: 3);

        CharacterPersonalityAdaptationResult? finalResult = null;

        // Run 3 consistent positive social interactions (threshold = 3)
        for (int i = 0; i < 3; i++)
        {
            var context = new CharacterCognitiveCycleContext(
                CycleId: Guid.NewGuid(),
                ExecutionId: Guid.NewGuid(),
                CharacterId: charId,
                TriggeredAtUtc: DateTimeOffset.UtcNow.AddMinutes(i)
            );
            var result = CreateSuccessResult(context, trustDelta: 2);
            finalResult = await service.ProcessAdaptationAsync(context, result);
        }

        // On the 3rd interaction, threshold is reached
        Assert.NotNull(finalResult);
        Assert.True(finalResult.AdaptationTriggered);
        Assert.Equal(50, finalResult.TraitValueBefore);
        Assert.Equal(51, finalResult.TraitValueAfter);
        Assert.Equal(+1, finalResult.TraitDelta);

        // Verify DB: Personality Warmth is 51, Version is 2
        var personality = await db.CharacterPersonalities.SingleAsync(p => p.CharacterId == charId);
        Assert.Equal(51, personality.Warmth);
        Assert.Equal(2u, personality.Version);

        // Verify DB: 1 adaptation record in audit ledger
        var adaptation = await db.CharacterPersonalityAdaptations.SingleAsync(a => a.CharacterId == charId);
        Assert.Equal(PersonalityTraitKeys.Warmth, adaptation.TraitKey);
        Assert.Equal(50, adaptation.ValueBefore);
        Assert.Equal(51, adaptation.ValueAfter);
        Assert.Equal(+1, adaptation.Delta);
        Assert.Equal(3, adaptation.EvidenceCount);

        // Verify all 3 evidence records are marked IsApplied == true
        var evidenceList = await db.CharacterPersonalityAdaptationEvidences.Where(e => e.CharacterId == charId).ToListAsync();
        Assert.Equal(3, evidenceList.Count);
        Assert.All(evidenceList, e => Assert.True(e.IsApplied));
        Assert.All(evidenceList, e => Assert.Equal(adaptation.Id, e.AdaptationId));
    }

    [Fact]
    public async Task ProcessAdaptationAsync_OppositeEvidence_CancelsOut_AndDoesNotMutatePersonality()
    {
        await using var db = new CoreDbContext(_options);
        var charId = await SeedCharacterStateAsync();
        var service = new PersonalityAdaptationService(
            db, new DefaultPersonalityAdaptationPolicy(), NullLogger<PersonalityAdaptationService>.Instance, threshold: 3);

        // 1. First interaction: Positive (+1 Warmth)
        var context1 = new CharacterCognitiveCycleContext(Guid.NewGuid(), Guid.NewGuid(), charId, DateTimeOffset.UtcNow);
        var res1 = await service.ProcessAdaptationAsync(context1, CreateSuccessResult(context1, trustDelta: 2));
        Assert.False(res1!.AdaptationTriggered);

        // 2. Second interaction: Negative (-1 Warmth)
        var context2 = new CharacterCognitiveCycleContext(Guid.NewGuid(), Guid.NewGuid(), charId, DateTimeOffset.UtcNow.AddMinutes(1));
        var res2 = await service.ProcessAdaptationAsync(context2, CreateSuccessResult(context2, trustDelta: -2));
        Assert.False(res2!.AdaptationTriggered);

        // 3. Third interaction: Positive (+1 Warmth) -> Net score = 1 - 1 + 1 = 1 (< 3)
        var context3 = new CharacterCognitiveCycleContext(Guid.NewGuid(), Guid.NewGuid(), charId, DateTimeOffset.UtcNow.AddMinutes(2));
        var res3 = await service.ProcessAdaptationAsync(context3, CreateSuccessResult(context3, trustDelta: 2));
        Assert.False(res3!.AdaptationTriggered);

        // 0 adaptations applied
        var adaptationsCount = await db.CharacterPersonalityAdaptations.CountAsync(a => a.CharacterId == charId);
        Assert.Equal(0, adaptationsCount);
    }

    #endregion

    #region 6. Optimistic Concurrency Tests (Two DbContexts)

    [Fact]
    public async Task Personality_VersionConcurrencyToken_WorkerAWins_WorkerBThrowsDbUpdateConcurrencyException()
    {
        var charId = await SeedCharacterStateAsync();

        // Seed initial personality at Version 1
        await using (var seedDb = new CoreDbContext(_options))
        {
            var initialPersonality = CharacterPersonality.CreateDefault(charId);
            await seedDb.CharacterPersonalities.AddAsync(initialPersonality);
            await seedDb.SaveChangesAsync();
            Assert.Equal(1u, initialPersonality.Version);
        }

        // Two independent contexts load the exact same personality at Version 1
        await using var dbWorkerA = new CoreDbContext(_options);
        await using var dbWorkerB = new CoreDbContext(_options);

        var personalityA = await dbWorkerA.CharacterPersonalities.SingleAsync(p => p.CharacterId == charId);
        var personalityB = await dbWorkerB.CharacterPersonalities.SingleAsync(p => p.CharacterId == charId);

        Assert.Equal(1u, personalityA.Version);
        Assert.Equal(1u, personalityB.Version);

        // Worker A adapts Warmth +1 -> Version advances to 2 and commits
        personalityA.AdaptTrait(PersonalityTraitKeys.Warmth, +1);
        await dbWorkerA.SaveChangesAsync();
        Assert.Equal(2u, personalityA.Version);

        // Worker B attempts to adapt Openness +1 starting from stale Version 1
        personalityB.AdaptTrait(PersonalityTraitKeys.Openness, +1);

        // Worker B MUST fail with DbUpdateConcurrencyException (proves no lost updates)
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbWorkerB.SaveChangesAsync());

        // Verify in independent context: Only Worker A's update was committed
        await using var verifyDb = new CoreDbContext(_options);
        var authoritative = await verifyDb.CharacterPersonalities.SingleAsync(p => p.CharacterId == charId);
        Assert.Equal(51, authoritative.Warmth);
        Assert.Equal(50, authoritative.Openness); // Worker B's update was rejected
        Assert.Equal(2u, authoritative.Version);
    }

    #endregion

    #region 7. Pipeline Integration & Invariant Tests

    [Fact]
    public async Task CognitiveCycle_FullExecution_ProducesPersonalityEvidence_AndThresholdAdaptation()
    {
        await using var db = new CoreDbContext(_options);
        var charId = await SeedCharacterStateAsync(hunger: 80m);
        var userId = Guid.NewGuid();

        var service = CreateService(db);

        // Run 3 cycles with positive user messages
        CharacterCognitiveCycleResult? lastResult = null;
        for (int i = 0; i < 3; i++)
        {
            var cycleContext = new CharacterCognitiveCycleContext(
                CycleId: Guid.NewGuid(),
                ExecutionId: Guid.NewGuid(),
                CharacterId: charId,
                TriggeredAtUtc: DateTimeOffset.UtcNow.AddMinutes(i),
                Event: new UserMessageCognitiveEvent(
                    EventId: Guid.NewGuid(),
                    CharacterId: charId,
                    OccurredAtUtc: DateTimeOffset.UtcNow.AddMinutes(i),
                    Message: "You did a fantastic job today!",
                    Source: "User",
                    UserId: userId
                )
            );

            lastResult = await service.RunAsync(cycleContext);
            Assert.True(lastResult.IsSuccess);
        }

        // On the 3rd cycle, personality adaptation must be present
        Assert.NotNull(lastResult);
        Assert.NotNull(lastResult.PersonalityAdaptation);
        Assert.True(lastResult.PersonalityAdaptation.AdaptationTriggered);
        Assert.Equal(51, lastResult.PersonalityAdaptation.TraitValueAfter);

        // Verify isolation: CharacterState was mutated properly (Hunger reduced), not overwritten by personality
        var state = await db.CharacterStates.SingleAsync(s => s.CharacterId == charId);
        Assert.True(state.Version > 1);

        // Verify relationship was updated
        var rel = await db.CharacterRelationships.SingleAsync(r => r.CharacterId == charId && r.TargetId == userId);
        Assert.True(rel.Trust > 0 || rel.Affection > 0 || rel.Familiarity > 0);

        // Verify authoritative personality is persisted
        var personality = await db.CharacterPersonalities.SingleAsync(p => p.CharacterId == charId);
        Assert.Equal(51, personality.Warmth);
    }

    #endregion

    #region 8. Failure Isolation Tests

    [Fact]
    public async Task CognitiveCycle_WhenPersonalityPersistenceFails_StateAndOtherFeedbacksRemainCommitted()
    {
        await using var db = new CoreDbContext(_options);
        var charId = await SeedCharacterStateAsync(hunger: 80m);
        var userId = Guid.NewGuid();

        // Inject failing personality adaptation service
        var failingPersonalityService = new FailingPersonalityAdaptationService();
        var service = CreateService(db, personalityAdaptationService: failingPersonalityService);

        var cycleContext = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            CharacterId: charId,
            TriggeredAtUtc: DateTimeOffset.UtcNow,
            Event: new UserMessageCognitiveEvent(
                EventId: Guid.NewGuid(),
                CharacterId: charId,
                OccurredAtUtc: DateTimeOffset.UtcNow,
                Message: "Hello there!",
                Source: "User",
                UserId: userId
            )
        );

        var result = await service.RunAsync(cycleContext);

        // Result is STILL successful despite personality persistence failure (non-fatal isolation)
        Assert.True(result.IsSuccess);
        Assert.Equal(CharacterCognitiveCycleStatus.CompletedWithAction, result.Status);

        // CharacterState transition was committed
        var state = await db.CharacterStates.SingleAsync(s => s.CharacterId == charId);
        Assert.True(state.Version > 1);

        // Relationship feedback was committed
        var rel = await db.CharacterRelationships.SingleOrDefaultAsync(r => r.CharacterId == charId && r.TargetId == userId);
        Assert.NotNull(rel);

        // Personality adaptation was isolated (null in result)
        Assert.Null(result.PersonalityAdaptation);
    }

    #endregion

    #region Helper Methods & Doubles

    private static CharacterCognitiveCycleResult CreateSuccessResult(
        CharacterCognitiveCycleContext context,
        int trustDelta = 1)
    {
        var actionProposal = new CharacterActionProposal(
            type: ActionType.Eat,
            intensity: 0.8,
            sourceIntent: IntentType.SeekFood,
            motivation: MotivationType.HungerDriven,
            stateVersion: 1
        );

        var actionExec = CharacterActionExecutionResult.Applied(
            context.ExecutionId,
            context.CharacterId,
            actionProposal,
            1,
            2,
            CharacterStateDelta.Zero,
            new CharacterStateSnapshot(energy: 80, hunger: 20, version: 2)
        );

        var relFeedback = new CharacterRelationshipFeedback(
            RelationshipId: Guid.NewGuid(),
            CharacterId: context.CharacterId,
            ExecutionId: context.ExecutionId,
            TargetId: Guid.NewGuid(),
            TargetType: RelationshipTargetType.User,
            TrustDelta: trustDelta,
            AffectionDelta: 0,
            FamiliarityDelta: 1,
            NewRelationshipType: null,
            Reason: "Helpful interaction",
            OccurredAtUtc: context.TriggeredAtUtc
        );

        var hungerPerception = new HungerPerception(HungerLevel.Satisfied, new PerceptionIntensity(0.2), 20);
        var energyPerception = new EnergyPerception(EnergyLevel.Energized, new PerceptionIntensity(0.2), 80);
        var moodPerception = new MoodPerception(MoodPerceptionLevel.Good, new PerceptionIntensity(0.6), 60, CharacterMood.Happy);
        var stressPerception = new StressPerception(StressLevel.Calm, new PerceptionIntensity(0.1), 10);
        var socialNeedPerception = new SocialNeedPerception(SocialNeedLevel.SociallySatisfied, new PerceptionIntensity(0.2), 20);
        var comfortPerception = new ComfortPerception(ComfortLevel.Comfortable, new PerceptionIntensity(0.2), 80);

        var experience = new CharacterInternalExperience(
            context.CharacterId,
            1,
            context.TriggeredAtUtc.UtcDateTime,
            hungerPerception,
            energyPerception,
            moodPerception,
            stressPerception,
            socialNeedPerception,
            comfortPerception,
            DominantNeed.None
        );

        var appraisal = new CharacterAppraisal(AppraisalType.SocialConnection, AppraisalPolarity.Positive, 0.7, AppraisalSource.SocialNeed);
        var emotion = new CharacterEmotion(EmotionType.Joy, 0.7, EmotionalValence.Positive, appraisal);

        var motivation = new CharacterMotivation(MotivationType.HungerDriven, 0.8, DesireSource.Hunger);
        var desire = new CharacterDesire(DesireType.NeedFood, 0.8, DesireSource.Hunger, motivation);
        var desires = new CharacterDesireEvaluation(context.CharacterId, 1, new[] { desire }, desire);

        var intent = new CharacterIntent(IntentType.SeekFood, 0.8, DesireType.NeedFood, MotivationType.HungerDriven, 1);
        var intentEval = new CharacterIntentEvaluation(context.CharacterId, 1, intent, context.TriggeredAtUtc);

        var proposalEval = new CharacterActionProposalEvaluation(context.CharacterId, 1, actionProposal, context.TriggeredAtUtc);

        return CharacterCognitiveCycleResult.CompletedWithAction(
            cycleId: context.CycleId,
            executionId: context.ExecutionId,
            characterId: context.CharacterId,
            triggeredAtUtc: context.TriggeredAtUtc,
            stateVersionAtStart: 1,
            experience: experience,
            appraisal: appraisal,
            emotion: emotion,
            desires: desires,
            intent: intentEval,
            actionProposal: proposalEval,
            actionExecution: actionExec,
            relationshipFeedback: relFeedback
        );
    }

    private sealed class FailingPersonalityAdaptationService : IPersonalityAdaptationService
    {
        public Task<CharacterPersonalityAdaptationResult?> ProcessAdaptationAsync(
            CharacterCognitiveCycleContext context,
            CharacterCognitiveCycleResult result,
            CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated database failure during personality adaptation persistence.");
        }
    }

    #endregion
}
