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
using Microsoft.EntityFrameworkCore.Storage;
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
        ICharacterRelationshipFeedbackService? relationshipFeedbackService = null,
        ICharacterPersonalityRepository? personalityRepository = null)
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

        var personalityRepo = personalityRepository ?? new CharacterPersonalityRepository(db);
        var personalityService = personalityAdaptationService ?? new PersonalityAdaptationService(
            personalityRepo,
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
            personalityAdaptationService: personalityService,
            personalityRepository: personalityRepo
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

    [Fact]
    public void CharacterPersonality_ToSnapshot_CreatesImmutableSnapshot()
    {
        var charId = Guid.NewGuid();
        var personality = new CharacterPersonality(
            charId,
            warmth: 65,
            openness: 70,
            assertiveness: 45,
            conscientiousness: 80,
            socialConfidence: 60,
            trustDisposition: 55,
            emotionalStability: 75,
            version: 3
        );

        var snapshot = personality.ToSnapshot();

        Assert.Equal(charId, snapshot.CharacterId);
        Assert.Equal(65, snapshot.Warmth);
        Assert.Equal(70, snapshot.Openness);
        Assert.Equal(45, snapshot.Assertiveness);
        Assert.Equal(80, snapshot.Conscientiousness);
        Assert.Equal(60, snapshot.SocialConfidence);
        Assert.Equal(55, snapshot.TrustDisposition);
        Assert.Equal(75, snapshot.EmotionalStability);
        Assert.Equal(3u, snapshot.Version);

        // Mutating entity does not mutate existing snapshot
        personality.AdaptTrait(PersonalityTraitKeys.Warmth, +1);
        Assert.Equal(66, personality.Warmth);
        Assert.Equal(65, snapshot.Warmth);
    }

    [Fact]
    public void CharacterPersonalitySnapshot_ToEffectivePsychology_ModulatesSensitivities()
    {
        var basePsych = PsychologyProfile.Default;

        // High emotional stability (100) -> 0.5x multiplier on Stress & Mood
        var resilientSnapshot = new CharacterPersonalitySnapshot(
            Guid.NewGuid(),
            Version: 1,
            Warmth: 50,
            Openness: 50,
            Assertiveness: 50,
            Conscientiousness: 100, // 0.75x multiplier on fatigue
            SocialConfidence: 50,
            TrustDisposition: 50,
            EmotionalStability: 100,
            SnapshotAtUtc: DateTimeOffset.UtcNow
        );

        var resilientPsych = resilientSnapshot.ToEffectivePsychology(basePsych);
        Assert.Equal(0.50m, resilientPsych.StressSensitivity);
        Assert.Equal(0.50m, resilientPsych.MoodReactivity);
        Assert.Equal(0.75m, resilientPsych.FatigueSensitivity);
        Assert.Equal(1.00m, resilientPsych.SocialSensitivity);

        // Low emotional stability (0) -> 1.5x multiplier on Stress & Mood
        var vulnerableSnapshot = new CharacterPersonalitySnapshot(
            Guid.NewGuid(),
            Version: 1,
            Warmth: 0,
            Openness: 50,
            Assertiveness: 50,
            Conscientiousness: 0, // 1.25x multiplier on fatigue
            SocialConfidence: 0,
            TrustDisposition: 50,
            EmotionalStability: 0,
            SnapshotAtUtc: DateTimeOffset.UtcNow
        );

        var vulnerablePsych = vulnerableSnapshot.ToEffectivePsychology(basePsych);
        Assert.Equal(1.50m, vulnerablePsych.StressSensitivity);
        Assert.Equal(1.50m, vulnerablePsych.MoodReactivity);
        Assert.Equal(1.25m, vulnerablePsych.FatigueSensitivity);
        Assert.Equal(0.50m, vulnerablePsych.SocialSensitivity);
    }

    #endregion

    #region 2. Evidence & Fingerprint Tests

    [Fact]
    public void CanonicalPersonalityFingerprint_ReasonWordingChange_ProducesIdenticalFingerprint()
    {
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();

        // Two reasons describing the exact same event with different human wording
        var fp = CanonicalPersonalityFingerprint.ComputeEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1);

        var ev1 = new PersonalityAdaptationEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1, "User complimented character warmly.", fp);

        var ev2 = new PersonalityAdaptationEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1, "User gave pleasant feedback.", fp);

        // Semantic fingerprint MUST be identical regardless of reason string (P1-4)
        Assert.NotEqual(ev1.Reason, ev2.Reason);
        Assert.Equal(ev1.Fingerprint, ev2.Fingerprint);
    }

    [Fact]
    public void CanonicalPersonalityFingerprint_DifferentPayloads_ProduceDifferentFingerprints()
    {
        var charId = Guid.NewGuid();
        var execId = Guid.NewGuid();

        var hash1 = CanonicalPersonalityFingerprint.ComputeEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1);

        var hash2 = CanonicalPersonalityFingerprint.ComputeEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.NegativeSocialOutcome,
            PersonalityTraitKeys.Warmth, -1, 1);

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
    public void Policy_GenericSuccessfulActions_ReturnNull_NoFalseLearning()
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

        // Standard actions (Eat, Rest, SeekComfort) without deliberate stress reduction or relationship feedback
        var actionsToTest = new[] { ActionType.Eat, ActionType.Rest, ActionType.SeekComfort };
        foreach (var action in actionsToTest)
        {
            var result = CreateSuccessResult(context, actionType: action, relFeedback: null);
            var evidence = policy.Evaluate(context, result);

            // MUST be empty: regular routine actions do not adapt long-term traits (P1-1)
            Assert.Empty(evidence);
        }
    }

    [Fact]
    public void Policy_EmotionalRegulation_OnlyTriggersOnDeliberateStressReliefUnderStress()
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

        // Case A: Deliberate ActionType.ReduceStress when stress is HighlyStressed and stress was reduced
        var validResult = CreateSuccessResult(
            context,
            actionType: ActionType.ReduceStress,
            stressLevel: StressLevel.HighlyStressed,
            stressDelta: -15m,
            relFeedback: null
        );
        var validEvidenceList = policy.Evaluate(context, validResult);
        var validEvidence = Assert.Single(validEvidenceList);
        Assert.Equal(PersonalityAdaptationEvidenceType.EmotionalRegulation, validEvidence.EvidenceType);
        Assert.Equal(PersonalityTraitKeys.EmotionalStability, validEvidence.TraitKey);
        Assert.Equal(+1, validEvidence.Direction);

        // Case B: ActionType.Rest (not ReduceStress), even if stress decreased
        var restResult = CreateSuccessResult(
            context,
            actionType: ActionType.Rest,
            stressLevel: StressLevel.HighlyStressed,
            stressDelta: -15m,
            relFeedback: null
        );
        Assert.Empty(policy.Evaluate(context, restResult));

        // Case C: ActionType.ReduceStress, but character was calm (no elevated stress)
        var calmResult = CreateSuccessResult(
            context,
            actionType: ActionType.ReduceStress,
            stressLevel: StressLevel.Calm,
            stressDelta: -5m,
            relFeedback: null
        );
        Assert.Empty(policy.Evaluate(context, calmResult));

        // Case D: ActionType.ReduceStress under HighlyStressed, but stress did NOT decrease
        var unreducedResult = CreateSuccessResult(
            context,
            actionType: ActionType.ReduceStress,
            stressLevel: StressLevel.HighlyStressed,
            stressDelta: 0m,
            relFeedback: null
        );
        Assert.Empty(policy.Evaluate(context, unreducedResult));
    }

    [Fact]
    public void Policy_RelationshipDeltas_MapToDistinctPersonalityDimensions()
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

        // 1. AffectionDelta > 0 -> Warmth +1
        var affPos = CreateSuccessResultWithRel(context, affectionDelta: 5);
        var evAffPos = Assert.Single(policy.Evaluate(context, affPos));
        Assert.Equal(PersonalityTraitKeys.Warmth, evAffPos.TraitKey);
        Assert.Equal(+1, evAffPos.Direction);

        // 2. AffectionDelta < 0 -> Warmth -1
        var affNeg = CreateSuccessResultWithRel(context, affectionDelta: -5);
        var evAffNeg = Assert.Single(policy.Evaluate(context, affNeg));
        Assert.Equal(PersonalityTraitKeys.Warmth, evAffNeg.TraitKey);
        Assert.Equal(-1, evAffNeg.Direction);

        // 3. TrustDelta > 0 -> TrustDisposition +1
        var trustPos = CreateSuccessResultWithRel(context, trustDelta: 5);
        var evTrustPos = Assert.Single(policy.Evaluate(context, trustPos));
        Assert.Equal(PersonalityTraitKeys.TrustDisposition, evTrustPos.TraitKey);
        Assert.Equal(+1, evTrustPos.Direction);

        // 4. TrustDelta < 0 -> TrustDisposition -1
        var trustNeg = CreateSuccessResultWithRel(context, trustDelta: -5);
        var evTrustNeg = Assert.Single(policy.Evaluate(context, trustNeg));
        Assert.Equal(PersonalityTraitKeys.TrustDisposition, evTrustNeg.TraitKey);
        Assert.Equal(-1, evTrustNeg.Direction);

        // 5. FamiliarityDelta > 0 -> SocialConfidence +1
        var famPos = CreateSuccessResultWithRel(context, familiarityDelta: 5);
        var evFamPos = Assert.Single(policy.Evaluate(context, famPos));
        Assert.Equal(PersonalityTraitKeys.SocialConfidence, evFamPos.TraitKey);
        Assert.Equal(+1, evFamPos.Direction);
    }

    [Fact]
    public void Policy_MultipleRelationshipDeltas_ProducesMultipleIndependentProposals()
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

        // Multi-delta interaction: Affection +5, Trust +5, Familiarity +5 produces 3 distinct proposals (P1-1)
        var multiResult = CreateSuccessResultWithRel(context, affectionDelta: 5, trustDelta: 5, familiarityDelta: 5);
        var proposals = policy.Evaluate(context, multiResult);

        Assert.Equal(3, proposals.Count);
        Assert.Contains(proposals, p => p.TraitKey == PersonalityTraitKeys.Warmth && p.Direction == +1);
        Assert.Contains(proposals, p => p.TraitKey == PersonalityTraitKeys.TrustDisposition && p.Direction == +1);
        Assert.Contains(proposals, p => p.TraitKey == PersonalityTraitKeys.SocialConfidence && p.Direction == +1);
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
        Assert.Empty(policy.Evaluate(context, conflict));

        var failed = CharacterCognitiveCycleResult.Failed(
            context.CycleId, execId, charId, context.TriggeredAtUtc, 1);
        Assert.Empty(policy.Evaluate(context, failed));

        var invalidInput = CharacterCognitiveCycleResult.InvalidInput(
            context.CycleId, execId, charId, context.TriggeredAtUtc, "Invalid");
        Assert.Empty(policy.Evaluate(context, invalidInput));

        var notFound = CharacterCognitiveCycleResult.NotFound(
            context.CycleId, execId, charId, context.TriggeredAtUtc, "NotFound");
        Assert.Empty(policy.Evaluate(context, notFound));

        var withoutAction = CharacterCognitiveCycleResult.CompletedWithoutAction(
            context.CycleId, execId, charId, context.TriggeredAtUtc, 1);
        Assert.Empty(policy.Evaluate(context, withoutAction));
    }


    #endregion

    #region 4. Idempotency & Repository Tests (P0-3, P1-4)

    [Fact]
    public async Task Repository_AddOrGetEvidenceAsync_WhenReplayedWithIdenticalPayload_IsIdempotent()
    {
        await using var db = new CoreDbContext(_options);
        var repo = new CharacterPersonalityRepository(db);
        var charId = await SeedCharacterStateAsync();
        var execId = Guid.NewGuid();

        var fp = CanonicalPersonalityFingerprint.ComputeEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1);

        var evidence1 = new PersonalityAdaptationEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1, "First attempt", fp);

        // First insert
        var stored1 = await repo.AddOrGetEvidenceAsync(evidence1);
        Assert.NotNull(stored1);
        Assert.NotEqual(Guid.Empty, stored1.Id);

        // Second insert with exact same semantics (even if reason wording differs, fp is same)
        var evidence2 = new PersonalityAdaptationEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1, "Wording variation", fp);

        var stored2 = await repo.AddOrGetEvidenceAsync(evidence2);
        Assert.NotNull(stored2);
        Assert.Equal(stored1.Id, stored2.Id);

        // Exactly 1 evidence record in DB
        var total = await db.CharacterPersonalityAdaptationEvidences.CountAsync(e => e.CharacterId == charId);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task Repository_SameExecutionId_CanPersistMultipleEvidenceRecordsForDifferentTraits()
    {
        await using var db = new CoreDbContext(_options);
        var repo = new CharacterPersonalityRepository(db);
        var charId = await SeedCharacterStateAsync();
        var execId = Guid.NewGuid();

        // 1. Evidence for Warmth
        var fpWarmth = CanonicalPersonalityFingerprint.ComputeEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1);
        var evWarmth = new PersonalityAdaptationEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1, "Warmth evidence", fpWarmth);
        await repo.AddOrGetEvidenceAsync(evWarmth);

        // 2. Evidence for TrustDisposition under same ExecutionId
        var fpTrust = CanonicalPersonalityFingerprint.ComputeEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.RepeatedSuccessfulInteraction,
            PersonalityTraitKeys.TrustDisposition, +1, 1);
        var evTrust = new PersonalityAdaptationEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.RepeatedSuccessfulInteraction,
            PersonalityTraitKeys.TrustDisposition, +1, 1, "Trust evidence", fpTrust);
        await repo.AddOrGetEvidenceAsync(evTrust);

        // 3. Evidence for SocialConfidence under same ExecutionId
        var fpSocial = CanonicalPersonalityFingerprint.ComputeEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.RepeatedSuccessfulInteraction,
            PersonalityTraitKeys.SocialConfidence, +1, 1);
        var evSocial = new PersonalityAdaptationEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.RepeatedSuccessfulInteraction,
            PersonalityTraitKeys.SocialConfidence, +1, 1, "Social evidence", fpSocial);
        await repo.AddOrGetEvidenceAsync(evSocial);

        // Verify: Exactly 3 evidence rows exist for this ExecutionId
        var evidences = await db.CharacterPersonalityAdaptationEvidences
            .Where(e => e.CharacterId == charId && e.ExecutionId == execId)
            .ToListAsync();
        Assert.Equal(3, evidences.Count);
        Assert.Contains(evidences, e => e.TraitKey == PersonalityTraitKeys.Warmth);
        Assert.Contains(evidences, e => e.TraitKey == PersonalityTraitKeys.TrustDisposition);
        Assert.Contains(evidences, e => e.TraitKey == PersonalityTraitKeys.SocialConfidence);
    }

    [Fact]
    public async Task Repository_SameExecutionId_CanPersistMultipleAdaptationRecordsForDifferentTraits()
    {
        await using var db = new CoreDbContext(_options);
        var repo = new CharacterPersonalityRepository(db);
        var charId = await SeedCharacterStateAsync();
        var execId = Guid.NewGuid();

        // 1. Adaptation for Warmth
        var adaptWarmth = new CharacterPersonalityAdaptation(
            characterId: charId,
            executionId: execId,
            traitKey: PersonalityTraitKeys.Warmth,
            valueBefore: 50,
            valueAfter: 51,
            delta: +1,
            evidenceCount: 3,
            fingerprint: CanonicalPersonalityFingerprint.ComputeAdaptation(charId, execId, PersonalityTraitKeys.Warmth, 50, 51, +1, 3),
            createdAtUtc: DateTime.UtcNow
        );
        await repo.AddAdaptationAsync(adaptWarmth);

        // 2. Adaptation for TrustDisposition under same ExecutionId
        var adaptTrust = new CharacterPersonalityAdaptation(
            characterId: charId,
            executionId: execId,
            traitKey: PersonalityTraitKeys.TrustDisposition,
            valueBefore: 50,
            valueAfter: 51,
            delta: +1,
            evidenceCount: 3,
            fingerprint: CanonicalPersonalityFingerprint.ComputeAdaptation(charId, execId, PersonalityTraitKeys.TrustDisposition, 50, 51, +1, 3),
            createdAtUtc: DateTime.UtcNow
        );
        await repo.AddAdaptationAsync(adaptTrust);

        await repo.SaveChangesAsync();

        // Verify: Exactly 2 adaptation rows exist for this ExecutionId
        var adaptations = await db.CharacterPersonalityAdaptations
            .Where(a => a.CharacterId == charId && a.ExecutionId == execId)
            .ToListAsync();
        Assert.Equal(2, adaptations.Count);
        Assert.Contains(adaptations, a => a.TraitKey == PersonalityTraitKeys.Warmth);
        Assert.Contains(adaptations, a => a.TraitKey == PersonalityTraitKeys.TrustDisposition);
    }

    [Fact]
    public async Task Repository_AddOrGetEvidenceAsync_SameExecutionIdDifferentTraitKey_DoesNotConflict()
    {
        await using var db = new CoreDbContext(_options);
        var repo = new CharacterPersonalityRepository(db);
        var charId = await SeedCharacterStateAsync();
        var execId = Guid.NewGuid();

        var fpWarmth = CanonicalPersonalityFingerprint.ComputeEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1);
        var evWarmth = new PersonalityAdaptationEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1, "Warmth", fpWarmth);
        var resWarmth = await repo.AddOrGetEvidenceAsync(evWarmth);

        // Even with opposite direction or different type, since TraitKey is different, no conflict
        var fpTrust = CanonicalPersonalityFingerprint.ComputeEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.RepeatedConflict,
            PersonalityTraitKeys.TrustDisposition, -1, 1);
        var evTrust = new PersonalityAdaptationEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.RepeatedConflict,
            PersonalityTraitKeys.TrustDisposition, -1, 1, "Trust conflict", fpTrust);
        var resTrust = await repo.AddOrGetEvidenceAsync(evTrust);

        Assert.NotNull(resWarmth);
        Assert.NotNull(resTrust);
        Assert.NotEqual(resWarmth.Id, resTrust.Id);
        Assert.Equal(PersonalityTraitKeys.Warmth, resWarmth.TraitKey);
        Assert.Equal(PersonalityTraitKeys.TrustDisposition, resTrust.TraitKey);
    }

    [Fact]
    public async Task Repository_AddOrGetEvidenceAsync_WhenReplayedWithDifferentSemanticPayload_ThrowsIdempotencyConflict()
    {
        await using var db = new CoreDbContext(_options);
        var repo = new CharacterPersonalityRepository(db);
        var charId = await SeedCharacterStateAsync();
        var execId = Guid.NewGuid();

        var fp1 = CanonicalPersonalityFingerprint.ComputeEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1);

        var evidence1 = new PersonalityAdaptationEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1, "First attempt", fp1);

        await repo.AddOrGetEvidenceAsync(evidence1);

        // Conflicting semantics with same ExecutionId
        var fp2 = CanonicalPersonalityFingerprint.ComputeEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.NegativeSocialOutcome,
            PersonalityTraitKeys.Warmth, -1, 1);

        var evidence2 = new PersonalityAdaptationEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.NegativeSocialOutcome,
            PersonalityTraitKeys.Warmth, -1, 1, "Second conflicting attempt", fp2);

        await Assert.ThrowsAsync<PersonalityAdaptationIdempotencyConflictException>(() =>
            repo.AddOrGetEvidenceAsync(evidence2));
    }

    [Fact]
    public async Task Repository_AddOrGetEvidenceAsync_WhenConcurrentInsertConflictOccurs_DetachesLocalAndReturnsWinner()
    {
        var charId = await SeedCharacterStateAsync();
        var execId = Guid.NewGuid();
        var fp = CanonicalPersonalityFingerprint.ComputeEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1);

        var interceptor = new ConcurrentPersonalityEvidenceInsertInterceptor(charId, execId, fp);
        var loserOptions = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;

        await using var dbLoser = new CoreDbContext(loserOptions);

        // Loser context tracks an unrelated entity to prove ChangeTracker is not indiscriminately cleared
        var unrelatedExecId = Guid.NewGuid();
        var unrelatedFp = CanonicalPersonalityFingerprint.ComputeEvidence(
            charId, unrelatedExecId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.SocialConfidence, +1, 1);
        var unrelatedEvidence = new PersonalityAdaptationEvidence(
            charId, unrelatedExecId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.SocialConfidence, +1, 1, "Unrelated", unrelatedFp);
        await dbLoser.CharacterPersonalityAdaptationEvidences.AddAsync(unrelatedEvidence);

        var candidate = new PersonalityAdaptationEvidence(
            charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1, "Race test", fp);

        var loserRepo = new CharacterPersonalityRepository(dbLoser);
        var resolved = await loserRepo.AddOrGetEvidenceAsync(candidate);

        // Assert 1: Interceptor actually injected concurrent winner
        Assert.True(interceptor.WasInjected);

        // Assert 2: Resolved evidence matches winner
        Assert.NotNull(resolved);
        Assert.Equal(charId, resolved.CharacterId);
        Assert.Equal(execId, resolved.ExecutionId);
        Assert.Equal(fp, resolved.Fingerprint);

        // Assert 3: Candidate was detached
        var candidateEntry = dbLoser.Entry(candidate);
        Assert.Equal(EntityState.Detached, candidateEntry.State);

        // Assert 4: Unrelated entity remains tracked as Added
        var unrelatedEntry = dbLoser.Entry(unrelatedEvidence);
        Assert.Equal(EntityState.Added, unrelatedEntry.State);

        // Assert 5: Subsequent SaveChangesAsync succeeds and saves unrelated entity
        await dbLoser.SaveChangesAsync();

        await using var verifyDb = new CoreDbContext(_options);
        var total = await verifyDb.CharacterPersonalityAdaptationEvidences.CountAsync(e => e.CharacterId == charId);
        Assert.Equal(2, total); // Winner + Unrelated
    }

    [Fact]
    public async Task Repository_AddOrGetAdaptationAsync_WhenReplayedWithIdenticalPayload_IsIdempotent()
    {
        await using var db = new CoreDbContext(_options);
        var repo = new CharacterPersonalityRepository(db);
        var charId = await SeedCharacterStateAsync();
        var execId = Guid.NewGuid();

        var fp = CanonicalPersonalityFingerprint.ComputeAdaptation(
            charId, execId, PersonalityTraitKeys.Warmth, 50, 51, +1, 3);

        var adaptation1 = new CharacterPersonalityAdaptation(
            charId, execId, PersonalityTraitKeys.Warmth, 50, 51, +1, 3, fp);

        var res1 = await repo.AddOrGetAdaptationAsync(adaptation1);

        var adaptation2 = new CharacterPersonalityAdaptation(
            charId, execId, PersonalityTraitKeys.Warmth, 50, 51, +1, 3, fp);

        var res2 = await repo.AddOrGetAdaptationAsync(adaptation2);

        Assert.NotNull(res1);
        Assert.NotNull(res2);
        Assert.Equal(res1.Id, res2.Id);
        Assert.Equal(fp, res2.Fingerprint);

        var total = await db.CharacterPersonalityAdaptations.CountAsync(a =>
            a.CharacterId == charId && a.ExecutionId == execId && a.TraitKey == PersonalityTraitKeys.Warmth);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task Repository_AddOrGetAdaptationAsync_WhenReplayedWithDifferentSemanticPayload_ThrowsIdempotencyConflict()
    {
        await using var db = new CoreDbContext(_options);
        var repo = new CharacterPersonalityRepository(db);
        var charId = await SeedCharacterStateAsync();
        var execId = Guid.NewGuid();

        var fp1 = CanonicalPersonalityFingerprint.ComputeAdaptation(
            charId, execId, PersonalityTraitKeys.Warmth, 50, 51, +1, 3);

        var adaptation1 = new CharacterPersonalityAdaptation(
            charId, execId, PersonalityTraitKeys.Warmth, 50, 51, +1, 3, fp1);

        await repo.AddOrGetAdaptationAsync(adaptation1);

        // Conflicting semantics (e.g. delta = -1 instead of +1, 50 -> 49) with same ExecutionId and TraitKey
        var fp2 = CanonicalPersonalityFingerprint.ComputeAdaptation(
            charId, execId, PersonalityTraitKeys.Warmth, 50, 49, -1, 3);

        var adaptation2 = new CharacterPersonalityAdaptation(
            charId, execId, PersonalityTraitKeys.Warmth, 50, 49, -1, 3, fp2);

        await Assert.ThrowsAsync<PersonalityAdaptationIdempotencyConflictException>(() =>
            repo.AddOrGetAdaptationAsync(adaptation2));
    }

    [Fact]
    public async Task Repository_AddOrGetAdaptationAsync_WhenConcurrentInsertConflictOccurs_DetachesLocalAndReturnsWinner()
    {
        var charId = await SeedCharacterStateAsync();
        var execId = Guid.NewGuid();
        var fp = CanonicalPersonalityFingerprint.ComputeAdaptation(
            charId, execId, PersonalityTraitKeys.Warmth, 50, 51, +1, 3);

        var interceptor = new ConcurrentPersonalityAdaptationInsertInterceptor(
            charId, execId, PersonalityTraitKeys.Warmth, 50, 51, +1, 3, fp);
        var loserOptions = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;

        await using var dbLoser = new CoreDbContext(loserOptions);

        // Loser context tracks an unrelated entity to prove ChangeTracker is not indiscriminately cleared
        var unrelatedExecId = Guid.NewGuid();
        var unrelatedFp = CanonicalPersonalityFingerprint.ComputeAdaptation(
            charId, unrelatedExecId, PersonalityTraitKeys.TrustDisposition, 50, 51, +1, 3);
        var unrelatedAdaptation = new CharacterPersonalityAdaptation(
            charId, unrelatedExecId, PersonalityTraitKeys.TrustDisposition, 50, 51, +1, 3, unrelatedFp);
        await dbLoser.CharacterPersonalityAdaptations.AddAsync(unrelatedAdaptation);

        var candidate = new CharacterPersonalityAdaptation(
            charId, execId, PersonalityTraitKeys.Warmth, 50, 51, +1, 3, fp);

        var loserRepo = new CharacterPersonalityRepository(dbLoser);
        var resolved = await loserRepo.AddOrGetAdaptationAsync(candidate);

        // Assert 1: Interceptor actually injected concurrent winner
        Assert.True(interceptor.WasInjected);

        // Assert 2: Resolved adaptation matches winner
        Assert.NotNull(resolved);
        Assert.Equal(charId, resolved.CharacterId);
        Assert.Equal(execId, resolved.ExecutionId);
        Assert.Equal(PersonalityTraitKeys.Warmth, resolved.TraitKey);
        Assert.Equal(fp, resolved.Fingerprint);

        // Assert 3: Candidate was detached
        var candidateEntry = dbLoser.Entry(candidate);
        Assert.Equal(EntityState.Detached, candidateEntry.State);

        // Assert 4: Unrelated entity remains tracked as Added
        var unrelatedEntry = dbLoser.Entry(unrelatedAdaptation);
        Assert.Equal(EntityState.Added, unrelatedEntry.State);

        // Assert 5: Subsequent SaveChangesAsync succeeds and saves unrelated entity
        await dbLoser.SaveChangesAsync();

        await using var verifyDb = new CoreDbContext(_options);
        var total = await verifyDb.CharacterPersonalityAdaptations.CountAsync(a => a.CharacterId == charId);
        Assert.Equal(2, total); // Winner + Unrelated
    }

    [Fact]
    public async Task Repository_AddOrGetAdaptationAsync_WhenConcurrentInsertConflictOccursWithDivergentFingerprint_ThrowsIdempotencyConflict()
    {
        var charId = await SeedCharacterStateAsync();
        var execId = Guid.NewGuid();

        // Injected winner has delta = +1
        var winnerFp = CanonicalPersonalityFingerprint.ComputeAdaptation(
            charId, execId, PersonalityTraitKeys.Warmth, 50, 51, +1, 3);

        var interceptor = new ConcurrentPersonalityAdaptationInsertInterceptor(
            charId, execId, PersonalityTraitKeys.Warmth, 50, 51, +1, 3, winnerFp);
        var loserOptions = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;

        await using var dbLoser = new CoreDbContext(loserOptions);

        // Candidate has delta = -1 (divergent payload)
        var candidateFp = CanonicalPersonalityFingerprint.ComputeAdaptation(
            charId, execId, PersonalityTraitKeys.Warmth, 50, 49, -1, 3);

        var candidate = new CharacterPersonalityAdaptation(
            charId, execId, PersonalityTraitKeys.Warmth, 50, 49, -1, 3, candidateFp);

        var loserRepo = new CharacterPersonalityRepository(dbLoser);

        await Assert.ThrowsAsync<PersonalityAdaptationIdempotencyConflictException>(() =>
            loserRepo.AddOrGetAdaptationAsync(candidate));

        // Candidate was detached after conflict
        var candidateEntry = dbLoser.Entry(candidate);
        Assert.Equal(EntityState.Detached, candidateEntry.State);
    }

    [Fact]
    public async Task Repository_AddAdaptationAsync_WhenFingerprintDoesNotMatchCanonical_ThrowsIdempotencyConflict()
    {
        await using var db = new CoreDbContext(_options);
        var repo = new CharacterPersonalityRepository(db);
        var charId = await SeedCharacterStateAsync();
        var execId = Guid.NewGuid();

        var invalidAdaptation = new CharacterPersonalityAdaptation(
            charId, execId, PersonalityTraitKeys.Warmth, 50, 51, +1, 3, "tampered_fake_fingerprint");

        await Assert.ThrowsAsync<PersonalityAdaptationIdempotencyConflictException>(() =>
            repo.AddAdaptationAsync(invalidAdaptation));
    }

    [Fact]
    public async Task Repository_AddOrGetAdaptationAsync_WhenIncomingFingerprintDoesNotMatchCanonical_ThrowsIdempotencyConflict()
    {
        await using var db = new CoreDbContext(_options);
        var repo = new CharacterPersonalityRepository(db);
        var charId = await SeedCharacterStateAsync();
        var execId = Guid.NewGuid();

        // Fingerprint generated from a DIFFERENT semantic payload
        var differentPayloadFp = CanonicalPersonalityFingerprint.ComputeAdaptation(
            charId, execId, PersonalityTraitKeys.Warmth, 50, 49, -1, 3);

        // Incoming adaptation payload (50 -> 51, Delta +1, EvidenceCount 3) paired with the mismatched fingerprint
        var adaptation = new CharacterPersonalityAdaptation(
            charId, execId, PersonalityTraitKeys.Warmth, 50, 51, +1, 3, differentPayloadFp);

        await Assert.ThrowsAsync<PersonalityAdaptationIdempotencyConflictException>(() =>
            repo.AddOrGetAdaptationAsync(adaptation));

        // Ensure no adaptation row was persisted
        var total = await db.CharacterPersonalityAdaptations.CountAsync(a => a.CharacterId == charId);
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task ProcessAdaptationAsync_WhenExistingAdaptationHasCorruptedOrConflictingFingerprint_ThrowsIdempotencyConflict()
    {
        await using var db = new CoreDbContext(_options);
        var charId = await SeedCharacterStateAsync();
        var execId = Guid.NewGuid();

        // Seed an adaptation in DB directly with a corrupted/divergent fingerprint
        var corruptedAdaptation = new CharacterPersonalityAdaptation(
            charId, execId, PersonalityTraitKeys.Warmth, 50, 51, +1, 3, "corrupted_invalid_hash");
        await db.CharacterPersonalityAdaptations.AddAsync(corruptedAdaptation);
        await db.SaveChangesAsync();

        var repo = new CharacterPersonalityRepository(db);
        var service = new PersonalityAdaptationService(
            repo, new DefaultPersonalityAdaptationPolicy(), NullLogger<PersonalityAdaptationService>.Instance, threshold: 3);

        var context = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: execId,
            CharacterId: charId,
            TriggeredAtUtc: DateTimeOffset.UtcNow
        );
        var result = CreateSuccessResultWithRel(context, affectionDelta: 5);

        await Assert.ThrowsAsync<PersonalityAdaptationIdempotencyConflictException>(() =>
            service.ProcessAdaptationAsync(context, result));
    }

    #endregion

    #region 5. Aggregation & Threshold Tests (P0-2)

    [Fact]
    public async Task ProcessAdaptationAsync_SingleInteraction_ProducesEvidence_DoesNotCauseAdaptation()
    {
        await using var db = new CoreDbContext(_options);
        var charId = await SeedCharacterStateAsync();
        var repo = new CharacterPersonalityRepository(db);
        var service = new PersonalityAdaptationService(
            repo, new DefaultPersonalityAdaptationPolicy(), NullLogger<PersonalityAdaptationService>.Instance, threshold: 3);

        var context = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            CharacterId: charId,
            TriggeredAtUtc: DateTimeOffset.UtcNow
        );
        var result = CreateSuccessResultWithRel(context, affectionDelta: 3);
        var adaptationResult = await service.ProcessAdaptationAsync(context, result);

        Assert.NotNull(adaptationResult);
        Assert.False(adaptationResult.AdaptationTriggered);

        // 1 evidence record in DB
        var evidenceCount = await db.CharacterPersonalityAdaptationEvidences.CountAsync(e => e.CharacterId == charId);
        Assert.Equal(1, evidenceCount);

        // 0 adaptations applied
        var adaptationsCount = await db.CharacterPersonalityAdaptations.CountAsync(a => a.CharacterId == charId);
        Assert.Equal(0, adaptationsCount);
    }

    [Fact]
    public async Task ProcessAdaptationAsync_WhenThresholdReached_MutatesPersonalityAndRecordsAdaptation()
    {
        await using var db = new CoreDbContext(_options);
        var charId = await SeedCharacterStateAsync();
        var repo = new CharacterPersonalityRepository(db);
        var service = new PersonalityAdaptationService(
            repo, new DefaultPersonalityAdaptationPolicy(), NullLogger<PersonalityAdaptationService>.Instance, threshold: 3);

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
            var result = CreateSuccessResultWithRel(context, affectionDelta: 3);
            finalResult = await service.ProcessAdaptationAsync(context, result);
        }

        Assert.NotNull(finalResult);
        Assert.True(finalResult.AdaptationTriggered);
        Assert.Equal(50, finalResult.TraitValueBefore);
        Assert.Equal(51, finalResult.TraitValueAfter);
        Assert.Equal(+1, finalResult.TraitDelta);

        // Authoritative personality in DB: Warmth is 51, Version is 2
        var personality = await db.CharacterPersonalities.SingleAsync(p => p.CharacterId == charId);
        Assert.Equal(51, personality.Warmth);
        Assert.Equal(2u, personality.Version);

        // 1 adaptation record in DB
        var adaptation = await db.CharacterPersonalityAdaptations.SingleAsync(a => a.CharacterId == charId);
        Assert.Equal(PersonalityTraitKeys.Warmth, adaptation.TraitKey);
        Assert.Equal(50, adaptation.ValueBefore);
        Assert.Equal(51, adaptation.ValueAfter);
        Assert.Equal(+1, adaptation.Delta);
        Assert.Equal(3, adaptation.EvidenceCount);

        // All 3 evidence records marked applied
        var evidenceList = await db.CharacterPersonalityAdaptationEvidences.Where(e => e.CharacterId == charId).ToListAsync();
        Assert.Equal(3, evidenceList.Count);
        Assert.All(evidenceList, e => Assert.True(e.IsApplied));
        Assert.All(evidenceList, e => Assert.Equal(adaptation.Id, e.AdaptationId));
    }

    [Fact]
    public async Task ProcessAdaptationAsync_ConcurrentThresholdEvaluation_ProducesExactlyOneAdaptation_NoLostUpdates()
    {
        var charId = await SeedCharacterStateAsync();

        // 1. Seed 2 unapplied evidence items in the database
        await using (var seedDb = new CoreDbContext(_options))
        {
            var seedRepo = new CharacterPersonalityRepository(seedDb);
            for (int i = 0; i < 2; i++)
            {
                var execId = Guid.NewGuid();
                var fp = CanonicalPersonalityFingerprint.ComputeEvidence(
                    charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
                    PersonalityTraitKeys.Warmth, +1, 1);
                var ev = new PersonalityAdaptationEvidence(
                    charId, execId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
                    PersonalityTraitKeys.Warmth, +1, 1, $"Preseeded {i}", fp);
                await seedRepo.AddOrGetEvidenceAsync(ev);
            }
        }

        var contextA = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            CharacterId: charId,
            TriggeredAtUtc: DateTimeOffset.UtcNow
        );

        var contextB = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            CharacterId: charId,
            TriggeredAtUtc: DateTimeOffset.UtcNow.AddSeconds(1)
        );
        var resultB = CreateSuccessResultWithRel(contextB, affectionDelta: 3);

        // 2. Setup Worker B with a SaveChangesInterceptor that deterministicly pauses Worker B
        // right before committing adaptation, allowing Worker A to race in and consume the evidence.
        var interceptor = new ConcurrentThresholdAdaptationInterceptor(async () =>
        {
            // Worker A reads the same 3 unapplied evidence items, adapts Warmth 50 -> 51, and commits
            await using var dbWorkerA = new CoreDbContext(_options);
            var repoA = new CharacterPersonalityRepository(dbWorkerA);
            var workerA_Evidence = await repoA.GetUnappliedEvidenceAsync(charId, PersonalityTraitKeys.Warmth);
            Assert.Equal(3, workerA_Evidence.Count);

            var personalityA = await repoA.GetOrCreateDefaultAsync(charId);
            var (beforeVal, afterVal) = personalityA.AdaptTrait(PersonalityTraitKeys.Warmth, +1);

            var adaptA = new CharacterPersonalityAdaptation(
                characterId: charId,
                executionId: contextA.ExecutionId,
                traitKey: PersonalityTraitKeys.Warmth,
                valueBefore: beforeVal,
                valueAfter: afterVal,
                delta: +1,
                evidenceCount: workerA_Evidence.Count,
                fingerprint: CanonicalPersonalityFingerprint.ComputeAdaptation(charId, contextA.ExecutionId, PersonalityTraitKeys.Warmth, beforeVal, afterVal, +1, workerA_Evidence.Count),
                createdAtUtc: DateTime.UtcNow
            );

            await repoA.AddAdaptationAsync(adaptA);
            foreach (var e in workerA_Evidence)
            {
                e.MarkApplied(adaptA.Id);
            }

            await repoA.SaveChangesAsync();
        });

        var optionsB = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;

        await using var dbWorkerB = new CoreDbContext(optionsB);
        var serviceB = new PersonalityAdaptationService(
            new CharacterPersonalityRepository(dbWorkerB),
            new DefaultPersonalityAdaptationPolicy(),
            NullLogger<PersonalityAdaptationService>.Instance,
            threshold: 3);

        // Worker B executes. Worker B reads 3 unapplied evidence items, pauses right before SaveChangesAsync.
        // Worker A commits the 3 items. Worker B catches DbUpdateConcurrencyException, clears tracking,
        // re-queries unapplied evidence, finds 0 remaining (< threshold 3), and exits cleanly with NoAdaptation.
        var resB = await serviceB.ProcessAdaptationAsync(contextB, resultB);

        Assert.True(interceptor.InterceptorFired);
        Assert.NotNull(resB);
        Assert.False(resB.AdaptationTriggered);

        // 3. Verify final state in an independent DbContext:
        // Warmth == 51, Adaptations.Count == 1, AppliedEvidence.Count == 3, UnappliedEvidence.Count == 0 (P0-1)
        await using var verifyDb = new CoreDbContext(_options);
        var personality = await verifyDb.CharacterPersonalities.SingleAsync(p => p.CharacterId == charId);
        Assert.Equal(51, personality.Warmth);
        Assert.Equal(2u, personality.Version);

        var totalAdaptations = await verifyDb.CharacterPersonalityAdaptations.CountAsync(a => a.CharacterId == charId);
        Assert.Equal(1, totalAdaptations);

        var appliedEvidence = await verifyDb.CharacterPersonalityAdaptationEvidences.CountAsync(e => e.CharacterId == charId && e.IsApplied);
        Assert.Equal(3, appliedEvidence);

        var unappliedEvidence = await verifyDb.CharacterPersonalityAdaptationEvidences.CountAsync(e => e.CharacterId == charId && !e.IsApplied);
        Assert.Equal(0, unappliedEvidence);
    }

    [Fact]
    public async Task ProcessAdaptationsAsync_MultiEvidenceCycle_PersistsAllEvidenceAndAdaptsMultipleTraits()
    {
        await using var db = new CoreDbContext(_options);
        var charId = await SeedCharacterStateAsync();
        var repo = new CharacterPersonalityRepository(db);
        var service = new PersonalityAdaptationService(
            repo, new DefaultPersonalityAdaptationPolicy(), NullLogger<PersonalityAdaptationService>.Instance, threshold: 3);

        // Preseed 2 unapplied evidence items for Warmth and 2 for TrustDisposition
        for (int i = 0; i < 2; i++)
        {
            var exec1 = Guid.NewGuid();
            var fpWarmth = CanonicalPersonalityFingerprint.ComputeEvidence(
                charId, exec1, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
                PersonalityTraitKeys.Warmth, +1, 1);
            await repo.AddOrGetEvidenceAsync(new PersonalityAdaptationEvidence(
                charId, exec1, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
                PersonalityTraitKeys.Warmth, +1, 1, $"Preseeded Warmth {i}", fpWarmth));

            var exec2 = Guid.NewGuid();
            var fpTrust = CanonicalPersonalityFingerprint.ComputeEvidence(
                charId, exec2, PersonalityAdaptationEvidenceType.RepeatedSuccessfulInteraction,
                PersonalityTraitKeys.TrustDisposition, +1, 1);
            await repo.AddOrGetEvidenceAsync(new PersonalityAdaptationEvidence(
                charId, exec2, PersonalityAdaptationEvidenceType.RepeatedSuccessfulInteraction,
                PersonalityTraitKeys.TrustDisposition, +1, 1, $"Preseeded Trust {i}", fpTrust));
        }

        // Single execution context with interaction producing multiple deltas: Affection +5, Trust +5, Familiarity +5
        var context = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            CharacterId: charId,
            TriggeredAtUtc: DateTimeOffset.UtcNow
        );
        var result = CreateSuccessResultWithRel(context, affectionDelta: 5, trustDelta: 5, familiarityDelta: 5);

        // Process all adaptations for the cycle
        var results = await service.ProcessAdaptationsAsync(context, result);

        // 3 evidence proposals processed
        Assert.Equal(3, results.Count);

        // Both Warmth and TrustDisposition hit threshold 3 (2 preseeded + 1 current) and adapted
        var warmthResult = results.Single(r => r.TraitKey == PersonalityTraitKeys.Warmth);
        Assert.True(warmthResult.AdaptationTriggered);
        Assert.Equal(51, warmthResult.TraitValueAfter);

        var trustResult = results.Single(r => r.TraitKey == PersonalityTraitKeys.TrustDisposition);
        Assert.True(trustResult.AdaptationTriggered);
        Assert.Equal(51, trustResult.TraitValueAfter);

        // SocialConfidence has only 1 evidence item (current cycle), below threshold 3 -> no adaptation
        var socialResult = results.Single(r => r.TraitKey == PersonalityTraitKeys.SocialConfidence);
        Assert.False(socialResult.AdaptationTriggered);

        // Verify in database: Both adaptations persisted under the SAME ExecutionId without collision (P1-1)
        await using var verifyDb = new CoreDbContext(_options);
        var adaptations = await verifyDb.CharacterPersonalityAdaptations
            .Where(a => a.CharacterId == charId && a.ExecutionId == context.ExecutionId)
            .ToListAsync();
        Assert.Equal(2, adaptations.Count);
        Assert.Contains(adaptations, a => a.TraitKey == PersonalityTraitKeys.Warmth && a.Delta == 1);
        Assert.Contains(adaptations, a => a.TraitKey == PersonalityTraitKeys.TrustDisposition && a.Delta == 1);

        // Character personality has updated both traits
        var personality = await verifyDb.CharacterPersonalities.SingleAsync(p => p.CharacterId == charId);
        Assert.Equal(51, personality.Warmth);
        Assert.Equal(51, personality.TrustDisposition);
    }

    [Fact]
    public async Task ProcessAdaptationAsync_OppositeEvidence_CancelsOut_AndDoesNotMutatePersonality()
    {
        await using var db = new CoreDbContext(_options);
        var charId = await SeedCharacterStateAsync();
        var repo = new CharacterPersonalityRepository(db);
        var service = new PersonalityAdaptationService(
            repo, new DefaultPersonalityAdaptationPolicy(), NullLogger<PersonalityAdaptationService>.Instance, threshold: 3);

        // 1. Positive (+1 Warmth)
        var context1 = new CharacterCognitiveCycleContext(Guid.NewGuid(), Guid.NewGuid(), charId, DateTimeOffset.UtcNow);
        var res1 = await service.ProcessAdaptationAsync(context1, CreateSuccessResultWithRel(context1, affectionDelta: 3));
        Assert.False(res1!.AdaptationTriggered);

        // 2. Negative (-1 Warmth)
        var context2 = new CharacterCognitiveCycleContext(Guid.NewGuid(), Guid.NewGuid(), charId, DateTimeOffset.UtcNow.AddMinutes(1));
        var res2 = await service.ProcessAdaptationAsync(context2, CreateSuccessResultWithRel(context2, affectionDelta: -3));
        Assert.False(res2!.AdaptationTriggered);

        // 3. Positive (+1 Warmth) -> Net score = +1 -1 +1 = +1 (< 3)
        var context3 = new CharacterCognitiveCycleContext(Guid.NewGuid(), Guid.NewGuid(), charId, DateTimeOffset.UtcNow.AddMinutes(2));
        var res3 = await service.ProcessAdaptationAsync(context3, CreateSuccessResultWithRel(context3, affectionDelta: 3));
        Assert.False(res3!.AdaptationTriggered);

        // 0 adaptations applied
        var adaptationsCount = await db.CharacterPersonalityAdaptations.CountAsync(a => a.CharacterId == charId);
        Assert.Equal(0, adaptationsCount);
    }

    #endregion

    #region 6. Concurrency Token & Race Tests (P2-2, P0-2)

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

    [Fact]
    public async Task Personality_GetOrCreateDefaultAsync_WhenConcurrentInsertConflictOccurs_DetachesLocalAndReturnsWinner()
    {
        var charId = await SeedCharacterStateAsync();

        var interceptor = new ConcurrentPersonalityInsertInterceptor(charId);
        var loserOptions = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;

        await using var dbLoser = new CoreDbContext(loserOptions);

        // Loser context tracks an unrelated entity to prove ChangeTracker is not cleared
        var unrelatedExecId = Guid.NewGuid();
        var unrelatedFp = CanonicalPersonalityFingerprint.ComputeEvidence(
            charId, unrelatedExecId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1);
        var unrelated = new PersonalityAdaptationEvidence(
            charId, unrelatedExecId, PersonalityAdaptationEvidenceType.PositiveSocialOutcome,
            PersonalityTraitKeys.Warmth, +1, 1, "Unrelated", unrelatedFp);
        await dbLoser.CharacterPersonalityAdaptationEvidences.AddAsync(unrelated);

        var loserRepo = new CharacterPersonalityRepository(dbLoser);
        var resolved = await loserRepo.GetOrCreateDefaultAsync(charId);

        // Assert 1: Interceptor actually injected the concurrent row
        Assert.True(interceptor.WasInjected);

        // Assert 2: Authoritative winner personality is returned
        Assert.NotNull(resolved);
        Assert.Equal(charId, resolved.CharacterId);
        Assert.Equal(50, resolved.Warmth);

        // Assert 3: Unrelated entity is STILL tracked as Added (ChangeTracker.Clear() was NOT called)
        var unrelatedEntry = dbLoser.Entry(unrelated);
        Assert.Equal(EntityState.Added, unrelatedEntry.State);

        // Assert 4: Subsequent SaveChangesAsync succeeds
        await dbLoser.SaveChangesAsync();

        // Assert 5: Verify in independent context: Exactly 1 personality exists, unrelated entity committed
        await using var verifyDb = new CoreDbContext(_options);
        var count = await verifyDb.CharacterPersonalities.CountAsync(p => p.CharacterId == charId);
        Assert.Equal(1, count);

        var savedUnrelated = await verifyDb.CharacterPersonalityAdaptationEvidences.SingleOrDefaultAsync(e => e.ExecutionId == unrelatedExecId);
        Assert.NotNull(savedUnrelated);
    }

    #endregion

    #region 7. Blueprint & Snapshot Separation Tests (P0-1)

    [Fact]
    public async Task CognitiveCycle_LoadsAuthoritativePersonalitySnapshot_AndModulatesExperiencePolicy()
    {
        await using var db = new CoreDbContext(_options);
        var charId = await SeedCharacterStateAsync(stress: 60m); // Stress 60

        // Seed authoritative personality with high EmotionalStability (100) -> 0.5x stress sensitivity
        var personality = new CharacterPersonality(
            charId,
            warmth: 85,
            openness: 60,
            assertiveness: 70,
            conscientiousness: 90,
            socialConfidence: 75,
            trustDisposition: 80,
            emotionalStability: 100,
            version: 1
        );
        db.CharacterPersonalities.Add(personality);
        await db.SaveChangesAsync();

        var service = CreateService(db);

        var cycleContext = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            CharacterId: charId,
            TriggeredAtUtc: DateTimeOffset.UtcNow
        );

        var result = await service.RunAsync(cycleContext);

        Assert.True(result.IsSuccess);
        // Result MUST expose immutable PersonalitySnapshot
        Assert.NotNull(result.PersonalitySnapshot);
        Assert.Equal(charId, result.PersonalitySnapshot.CharacterId);
        Assert.Equal(85, result.PersonalitySnapshot.Warmth);
        Assert.Equal(100, result.PersonalitySnapshot.EmotionalStability);

        // Stress intensity MUST be modulated by 0.5x (60 / 100 * 0.5 = 0.30)
        Assert.NotNull(result.Experience);
        Assert.Equal(0.30, result.Experience.Stress.Intensity.Value, 2);
    }

    [Fact]
    public async Task CognitiveCycle_AdaptationOnlyAffectsFutureCycles()
    {
        await using var db = new CoreDbContext(_options);
        var charId = await SeedCharacterStateAsync(hunger: 80m);
        var userId = Guid.NewGuid();

        // Seed authoritative personality with initial Warmth = 50
        var personality = CharacterPersonality.CreateDefault(charId);
        db.CharacterPersonalities.Add(personality);
        await db.SaveChangesAsync();

        // Configure service with threshold = 1 so cycle 1 immediately adapts
        var repo = new CharacterPersonalityRepository(db);
        var personalityService = new PersonalityAdaptationService(
            repo,
            new DefaultPersonalityAdaptationPolicy(),
            NullLogger<PersonalityAdaptationService>.Instance,
            threshold: 1);

        var service = CreateService(db, personalityAdaptationService: personalityService, personalityRepository: repo);

        // --- Cycle 1 ---
        var cycle1Context = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            CharacterId: charId,
            TriggeredAtUtc: DateTimeOffset.UtcNow,
            Event: new UserMessageCognitiveEvent(
                EventId: Guid.NewGuid(),
                CharacterId: charId,
                OccurredAtUtc: DateTimeOffset.UtcNow,
                Message: "Great job, friend!",
                Source: "User",
                UserId: userId
            )
        );

        var result1 = await service.RunAsync(cycle1Context);
        Assert.True(result1.IsSuccess);

        // Cycle 1 evaluated using Snapshot A (Warmth = 50)
        Assert.NotNull(result1.PersonalitySnapshot);
        Assert.Equal(50, result1.PersonalitySnapshot.Warmth);

        // Adaptation triggered at the END of Cycle 1
        Assert.NotNull(result1.PersonalityAdaptation);
        Assert.True(result1.PersonalityAdaptation.AdaptationTriggered);
        Assert.Equal(50, result1.PersonalityAdaptation.TraitValueBefore);
        Assert.Equal(51, result1.PersonalityAdaptation.TraitValueAfter);

        // --- Cycle 2 ---
        var cycle2Context = new CharacterCognitiveCycleContext(
            CycleId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            CharacterId: charId,
            TriggeredAtUtc: DateTimeOffset.UtcNow.AddMinutes(1),
            Event: new UserMessageCognitiveEvent(
                EventId: Guid.NewGuid(),
                CharacterId: charId,
                OccurredAtUtc: DateTimeOffset.UtcNow.AddMinutes(1),
                Message: "Another greeting!",
                Source: "User",
                UserId: userId
            )
        );

        var result2 = await service.RunAsync(cycle2Context);
        Assert.True(result2.IsSuccess);

        // Cycle 2 evaluates using Snapshot B (Warmth = 51)
        Assert.NotNull(result2.PersonalitySnapshot);
        Assert.Equal(51, result2.PersonalitySnapshot.Warmth);
    }

    #endregion

    #region 8. Pipeline Integration & Failure Isolation Tests

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

    private static CharacterCognitiveCycleResult CreateSuccessResultWithRel(
        CharacterCognitiveCycleContext context,
        int trustDelta = 0,
        int affectionDelta = 0,
        int familiarityDelta = 0)
    {
        var relFeedback = new CharacterRelationshipFeedback(
            RelationshipId: Guid.NewGuid(),
            CharacterId: context.CharacterId,
            ExecutionId: context.ExecutionId,
            TargetId: Guid.NewGuid(),
            TargetType: RelationshipTargetType.User,
            TrustDelta: trustDelta,
            AffectionDelta: affectionDelta,
            FamiliarityDelta: familiarityDelta,
            NewRelationshipType: null,
            Reason: "Relationship test feedback",
            OccurredAtUtc: context.TriggeredAtUtc
        );

        return CreateSuccessResult(context, relFeedback: relFeedback);
    }

    private static CharacterCognitiveCycleResult CreateSuccessResult(
        CharacterCognitiveCycleContext context,
        ActionType actionType = ActionType.Eat,
        StressLevel stressLevel = StressLevel.Calm,
        decimal stressDelta = 0m,
        CharacterRelationshipFeedback? relFeedback = null)
    {
        var actionProposal = new CharacterActionProposal(
            type: actionType,
            intensity: 0.8,
            sourceIntent: IntentType.SeekFood,
            motivation: MotivationType.HungerDriven,
            stateVersion: 1
        );

        var appliedDelta = stressDelta != 0m
            ? new CharacterStateDelta { StressDelta = stressDelta }
            : CharacterStateDelta.Zero;

        var actionExec = CharacterActionExecutionResult.Applied(
            context.ExecutionId,
            context.CharacterId,
            actionProposal,
            1,
            2,
            appliedDelta,
            new CharacterStateSnapshot(energy: 80, hunger: 20, version: 2)
        );

        var hungerPerception = new HungerPerception(HungerLevel.Satisfied, new PerceptionIntensity(0.2), 20);
        var energyPerception = new EnergyPerception(EnergyLevel.Energized, new PerceptionIntensity(0.2), 80);
        var moodPerception = new MoodPerception(MoodPerceptionLevel.Good, new PerceptionIntensity(0.6), 60, CharacterMood.Happy);
        var stressPerception = new StressPerception(stressLevel, new PerceptionIntensity(0.1), 10);
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
        public Task<IReadOnlyList<CharacterPersonalityAdaptationResult>> ProcessAdaptationsAsync(
            CharacterCognitiveCycleContext context,
            CharacterCognitiveCycleResult result,
            CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated database failure during personality adaptation persistence.");
        }

        public Task<CharacterPersonalityAdaptationResult?> ProcessAdaptationAsync(
            CharacterCognitiveCycleContext context,
            CharacterCognitiveCycleResult result,
            CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated database failure during personality adaptation persistence.");
        }
    }

    private sealed class ConcurrentPersonalityInsertInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private readonly Guid _charId;
        public bool WasInjected { get; private set; }

        public ConcurrentPersonalityInsertInterceptor(Guid charId)
        {
            _charId = charId;
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!WasInjected && eventData.Context != null)
            {
                WasInjected = true;

                using var cmd = eventData.Context.Database.GetDbConnection().CreateCommand();
                if (eventData.Context.Database.CurrentTransaction != null)
                {
                    cmd.Transaction = eventData.Context.Database.CurrentTransaction.GetDbTransaction();
                }

                var id = Guid.NewGuid();
                var now = DateTime.UtcNow.ToString("O");
                cmd.CommandText = @"
                    INSERT INTO ""CharacterPersonalities"" (
                        ""Id"", ""CharacterId"", ""Warmth"", ""Openness"", ""Assertiveness"", ""Conscientiousness"",
                        ""SocialConfidence"", ""TrustDisposition"", ""EmotionalStability"", ""Version"", ""CreatedAt"", ""IsSoftDeleted""
                    ) VALUES (
                        @id, @charId, 50, 50, 50, 50, 50, 50, 50, 1, @now, 0
                    );";

                var pId = cmd.CreateParameter();
                pId.ParameterName = "@id";
                pId.Value = id;
                cmd.Parameters.Add(pId);

                var pCharId = cmd.CreateParameter();
                pCharId.ParameterName = "@charId";
                pCharId.Value = _charId;
                cmd.Parameters.Add(pCharId);

                var pNow = cmd.CreateParameter();
                pNow.ParameterName = "@now";
                pNow.Value = now;
                cmd.Parameters.Add(pNow);

                cmd.ExecuteNonQuery();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class ConcurrentPersonalityEvidenceInsertInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private readonly Guid _charId;
        private readonly Guid _execId;
        private readonly string _fingerprint;
        public bool WasInjected { get; private set; }

        public ConcurrentPersonalityEvidenceInsertInterceptor(Guid charId, Guid execId, string fingerprint)
        {
            _charId = charId;
            _execId = execId;
            _fingerprint = fingerprint;
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!WasInjected && eventData.Context != null)
            {
                WasInjected = true;

                using var cmd = eventData.Context.Database.GetDbConnection().CreateCommand();
                if (eventData.Context.Database.CurrentTransaction != null)
                {
                    cmd.Transaction = eventData.Context.Database.CurrentTransaction.GetDbTransaction();
                }

                var id = Guid.NewGuid();
                var now = DateTime.UtcNow.ToString("O");
                cmd.CommandText = @"
                    INSERT INTO ""CharacterPersonalityAdaptationEvidences"" (
                        ""Id"", ""CharacterId"", ""ExecutionId"", ""EvidenceType"", ""TraitKey"", ""Direction"",
                        ""Strength"", ""Reason"", ""Fingerprint"", ""IsApplied"", ""CreatedAtUtc""
                    ) VALUES (
                        @id, @charId, @execId, @type, @traitKey, 1,
                        1, 'Winner reason', @fingerprint, 0, @now
                    );";

                var pId = cmd.CreateParameter();
                pId.ParameterName = "@id";
                pId.Value = id;
                cmd.Parameters.Add(pId);

                var pCharId = cmd.CreateParameter();
                pCharId.ParameterName = "@charId";
                pCharId.Value = _charId;
                cmd.Parameters.Add(pCharId);

                var pExecId = cmd.CreateParameter();
                pExecId.ParameterName = "@execId";
                pExecId.Value = _execId;
                cmd.Parameters.Add(pExecId);

                var pType = cmd.CreateParameter();
                pType.ParameterName = "@type";
                pType.Value = nameof(PersonalityAdaptationEvidenceType.PositiveSocialOutcome);
                cmd.Parameters.Add(pType);

                var pTraitKey = cmd.CreateParameter();
                pTraitKey.ParameterName = "@traitKey";
                pTraitKey.Value = PersonalityTraitKeys.Warmth;
                cmd.Parameters.Add(pTraitKey);

                var pFp = cmd.CreateParameter();
                pFp.ParameterName = "@fingerprint";
                pFp.Value = _fingerprint;
                cmd.Parameters.Add(pFp);

                var pNow = cmd.CreateParameter();
                pNow.ParameterName = "@now";
                pNow.Value = now;
                cmd.Parameters.Add(pNow);

                cmd.ExecuteNonQuery();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class ConcurrentPersonalityAdaptationInsertInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private readonly Guid _charId;
        private readonly Guid _execId;
        private readonly string _traitKey;
        private readonly int _valueBefore;
        private readonly int _valueAfter;
        private readonly int _delta;
        private readonly int _evidenceCount;
        private readonly string _fingerprint;
        public bool WasInjected { get; private set; }

        public ConcurrentPersonalityAdaptationInsertInterceptor(
            Guid charId,
            Guid execId,
            string traitKey,
            int valueBefore,
            int valueAfter,
            int delta,
            int evidenceCount,
            string fingerprint)
        {
            _charId = charId;
            _execId = execId;
            _traitKey = traitKey;
            _valueBefore = valueBefore;
            _valueAfter = valueAfter;
            _delta = delta;
            _evidenceCount = evidenceCount;
            _fingerprint = fingerprint;
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!WasInjected && eventData.Context != null)
            {
                WasInjected = true;

                using var cmd = eventData.Context.Database.GetDbConnection().CreateCommand();
                if (eventData.Context.Database.CurrentTransaction != null)
                {
                    cmd.Transaction = eventData.Context.Database.CurrentTransaction.GetDbTransaction();
                }

                var id = Guid.NewGuid();
                var now = DateTime.UtcNow.ToString("O");
                cmd.CommandText = @"
                    INSERT INTO ""CharacterPersonalityAdaptations"" (
                        ""Id"", ""CharacterId"", ""ExecutionId"", ""TraitKey"", ""ValueBefore"", ""ValueAfter"",
                        ""Delta"", ""EvidenceCount"", ""Fingerprint"", ""CreatedAtUtc""
                    ) VALUES (
                        @id, @charId, @execId, @traitKey, @valueBefore, @valueAfter,
                        @delta, @evidenceCount, @fingerprint, @now
                    );";

                var pId = cmd.CreateParameter();
                pId.ParameterName = "@id";
                pId.Value = id;
                cmd.Parameters.Add(pId);

                var pCharId = cmd.CreateParameter();
                pCharId.ParameterName = "@charId";
                pCharId.Value = _charId;
                cmd.Parameters.Add(pCharId);

                var pExecId = cmd.CreateParameter();
                pExecId.ParameterName = "@execId";
                pExecId.Value = _execId;
                cmd.Parameters.Add(pExecId);

                var pTraitKey = cmd.CreateParameter();
                pTraitKey.ParameterName = "@traitKey";
                pTraitKey.Value = _traitKey;
                cmd.Parameters.Add(pTraitKey);

                var pValBefore = cmd.CreateParameter();
                pValBefore.ParameterName = "@valueBefore";
                pValBefore.Value = _valueBefore;
                cmd.Parameters.Add(pValBefore);

                var pValAfter = cmd.CreateParameter();
                pValAfter.ParameterName = "@valueAfter";
                pValAfter.Value = _valueAfter;
                cmd.Parameters.Add(pValAfter);

                var pDelta = cmd.CreateParameter();
                pDelta.ParameterName = "@delta";
                pDelta.Value = _delta;
                cmd.Parameters.Add(pDelta);

                var pCount = cmd.CreateParameter();
                pCount.ParameterName = "@evidenceCount";
                pCount.Value = _evidenceCount;
                cmd.Parameters.Add(pCount);

                var pFp = cmd.CreateParameter();
                pFp.ParameterName = "@fingerprint";
                pFp.Value = _fingerprint;
                cmd.Parameters.Add(pFp);

                var pNow = cmd.CreateParameter();
                pNow.ParameterName = "@now";
                pNow.Value = now;
                cmd.Parameters.Add(pNow);

                cmd.ExecuteNonQuery();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class ConcurrentThresholdAdaptationInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private readonly Func<Task> _onSavingAdaptationAsync;
        private int _fired = 0;

        public bool InterceptorFired => _fired == 1;

        public ConcurrentThresholdAdaptationInterceptor(Func<Task> onSavingAdaptationAsync)
        {
            _onSavingAdaptationAsync = onSavingAdaptationAsync;
        }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context != null &&
                eventData.Context.ChangeTracker.Entries<CharacterPersonalityAdaptation>().Any())
            {
                if (Interlocked.Exchange(ref _fired, 1) == 0)
                {
                    await _onSavingAdaptationAsync();
                }
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    #endregion
}

