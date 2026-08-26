using Application.DTOs;
using Application.Enums;
using Application.Services;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Tests.IdentityQualityGuard;

public sealed class IdentityQualityGuardPolicyTests
{
    [Fact]
    public void IdentityQualityGuardPolicy_Constructor_WhenParametersInvalid_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IdentityQualityGuardPolicy(MinAcceptableIdentitySimilarity: -0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IdentityQualityGuardPolicy(MinAcceptableIdentitySimilarity: 1.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IdentityQualityGuardPolicy(MaxAttempts: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IdentityQualityGuardPolicy(MaxAttempts: 4)); // max is 3
    }

    [Fact]
    public void IdentityQualityGuardPolicy_Constructor_PrecedenceRule_IdentitySimilarityTakesPrecedenceOverLegacyFaceSimilarity()
    {
        var policy = new IdentityQualityGuardPolicy(
            MinAcceptableIdentitySimilarity: 0.82f,
            MinAcceptableFaceSimilarity: 0.70f
        );

        Assert.Equal(0.82f, policy.MinAcceptableIdentitySimilarity);
#pragma warning disable CS0618
        Assert.Equal(0.82f, policy.MinAcceptableFaceSimilarity);
#pragma warning restore CS0618
    }

    [Fact]
    public void IdentityEvaluationResult_Pass_HasPassedStatus()
    {
        var result = IdentityEvaluationResult.Pass(identitySimilarity: 0.88f, featureScore: 0.95f, overallScore: 0.90f);
        Assert.Equal(IdentityStatus.Passed, result.Status);
        Assert.False(result.InvariantViolated);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void IdentityEvaluationResult_Degrade_HasDegradedStatus()
    {
        var violations = new[] { new IdentityViolation(ReferenceAuthorityScope.CanonicalIdentity, "LOW_IDENTITY_SIMILARITY", "Similarity below threshold", IsCritical: false) };
        var result = IdentityEvaluationResult.Degrade(0.71f, 0.80f, 0.74f, violations);

        Assert.Equal(IdentityStatus.Degraded, result.Status);
        Assert.False(result.InvariantViolated);
        Assert.Single(result.Violations);
    }

    [Fact]
    public void IdentityEvaluationResult_Fail_HasFailedStatusAndInvariantViolated()
    {
        var violations = new[] { new IdentityViolation(ReferenceAuthorityScope.CanonicalIdentity, "GENDER_INVARIANT_VIOLATION", "Female anatomical features on male knight", IsCritical: true) };
        var result = IdentityEvaluationResult.Fail(0.65f, 0.40f, 0.55f, violations);

        Assert.Equal(IdentityStatus.Failed, result.Status);
        Assert.True(result.InvariantViolated);
        Assert.Single(result.Violations);
    }

    [Fact]
    public void IdentityQualityGuardPolicy_DecideMitigation_EscalatesDeterministically()
    {
        var policy = new IdentityQualityGuardPolicy(MaxAttempts: 3);

        // Attempt 1 Degraded -> RetryAttenuated
        var evalDegraded = IdentityEvaluationResult.Degrade(0.70f, 0.80f, 0.74f, Array.Empty<IdentityViolation>());
        Assert.Equal(QualityMitigationAction.RetryAttenuated, policy.DecideMitigation(1, evalDegraded));

        // Attempt 1 Hard Invariant Failure -> RetryIsolated immediately
        var evalFail = IdentityEvaluationResult.Fail(0.60f, 0.40f, 0.50f, new[] { new IdentityViolation(ReferenceAuthorityScope.CanonicalIdentity, "CRIT", "desc", true) });
        Assert.Equal(QualityMitigationAction.RetryIsolated, policy.DecideMitigation(1, evalFail));

        // Attempt 2 Degraded -> RetryIsolated
        Assert.Equal(QualityMitigationAction.RetryIsolated, policy.DecideMitigation(2, evalDegraded));

        // Attempt 3 Degraded -> RejectDegraded
        Assert.Equal(QualityMitigationAction.RejectDegraded, policy.DecideMitigation(3, evalDegraded));

        // Any attempt Passed -> Pass
        var evalPassed = IdentityEvaluationResult.Pass(0.85f, 0.90f, 0.87f);
        Assert.Equal(QualityMitigationAction.Pass, policy.DecideMitigation(1, evalPassed));
        Assert.Equal(QualityMitigationAction.Pass, policy.DecideMitigation(2, evalPassed));
        Assert.Equal(QualityMitigationAction.Pass, policy.DecideMitigation(3, evalPassed));
    }

    [Theory]
    [InlineData("AiProviders:ImageGeneration:QualityGuard:MinFaceSimilarity", "1.5")]
    [InlineData("AiProviders:ImageGeneration:QualityGuard:MinIdentitySimilarity", "-0.2")]
    [InlineData("AiProviders:ImageGeneration:QualityGuard:MinFeatureScore", "2.0")]
    [InlineData("AiProviders:ImageGeneration:QualityGuard:MaxAttempts", "5")]
    [InlineData("AiProviders:ImageGeneration:QualityGuard:Enabled", "not_a_bool")]
    public void IdentityQualityGuardPolicy_FromConfiguration_InvalidValues_Throws(string key, string value)
    {
        var inMemory = new Dictionary<string, string?> { [key] = value };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();

        Assert.Throws<InvalidOperationException>(() => IdentityQualityGuardPolicy.FromConfiguration(config));
    }
}
