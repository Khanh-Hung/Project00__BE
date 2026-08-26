using Application.Common;
using Xunit;

namespace Tests.IdentityQualityGuard;

public sealed class DeterministicSeedDerivationTests
{
    [Fact]
    public void DeterministicSeedDerivation_Attempt1_ReturnsBaseSeedUnmodified()
    {
        long baseSeed = 42424242L;
        long derived = DeterministicSeedDerivation.Derive(baseSeed, attemptNumber: 1);
        Assert.Equal(baseSeed, derived);
    }

    [Fact]
    public void DeterministicSeedDerivation_HigherAttempts_ProduceDeterministicDistinctSeeds()
    {
        long baseSeed = 100000L;
        long seed1 = DeterministicSeedDerivation.Derive(baseSeed, attemptNumber: 1);
        long seed2 = DeterministicSeedDerivation.Derive(baseSeed, attemptNumber: 2);
        long seed3 = DeterministicSeedDerivation.Derive(baseSeed, attemptNumber: 3);

        Assert.Equal(100000L, seed1);
        Assert.NotEqual(seed1, seed2);
        Assert.NotEqual(seed2, seed3);
        Assert.NotEqual(seed1, seed3);

        // Exact reproducibility: Re-running produces identical sequence
        Assert.Equal(seed2, DeterministicSeedDerivation.Derive(baseSeed, attemptNumber: 2));
        Assert.Equal(seed3, DeterministicSeedDerivation.Derive(baseSeed, attemptNumber: 3));
    }

    [Fact]
    public void DeterministicSeedDerivation_DifferentBaseSeeds_ProduceDifferentDerivations()
    {
        long seedA = DeterministicSeedDerivation.Derive(111111L, 2);
        long seedB = DeterministicSeedDerivation.Derive(222222L, 2);
        Assert.NotEqual(seedA, seedB);
    }

    [Fact]
    public void DeterministicSeedDerivation_ProducesNonNegative64BitSeeds()
    {
        long[] baseSeeds = { 0L, 1L, 9999999999L, long.MaxValue - 1, 123456789L };
        foreach (var baseSeed in baseSeeds)
        {
            for (int attempt = 1; attempt <= 10; attempt++)
            {
                long derived = DeterministicSeedDerivation.Derive(baseSeed, attempt);
                Assert.True(derived >= 0, $"Derived seed for base {baseSeed} attempt {attempt} was negative: {derived}");
            }
        }
    }

    [Fact]
    public void DeterministicSeedDerivation_Fingerprint_IsDeterministicAndSensitiveToParameters()
    {
        var jobId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var fp1 = DeterministicSeedDerivation.ComputeFingerprint(
            jobId, turnId, sceneRevision: 1, attemptNumber: 1, derivedSeed: 12345L,
            parametersJson: "{\"ipAdapter\":{\"weight\":0.60}}",
            workflow: "VisualIdentity", workflowVersion: 1,
            compiledPrompt: "1man knight in courtyard", compiledNegativePrompt: "blurry",
            previousReferenceUrl: "https://cdn.project00.ai/prev.png");

        var fp2 = DeterministicSeedDerivation.ComputeFingerprint(
            jobId, turnId, sceneRevision: 1, attemptNumber: 1, derivedSeed: 12345L,
            parametersJson: "{\"ipAdapter\":{\"weight\":0.60}}",
            workflow: "VisualIdentity", workflowVersion: 1,
            compiledPrompt: "1man knight in courtyard", compiledNegativePrompt: "blurry",
            previousReferenceUrl: "https://cdn.project00.ai/prev.png");

        Assert.Equal(fp1, fp2);
        Assert.Equal(64, fp1.Length); // 64 hex characters (SHA-256)
    }

    [Fact]
    public void DeterministicSeedDerivation_Fingerprint_SensitiveToPrompt_Negative_Workflow_And_Reference()
    {
        var jobId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var baseFp = DeterministicSeedDerivation.ComputeFingerprint(
            jobId, turnId, 1, 1, 12345L, "{}", "VisualIdentity", 1, "pos", "neg", "https://cdn.project00.ai/ref.png");

        var diffPrompt = DeterministicSeedDerivation.ComputeFingerprint(
            jobId, turnId, 1, 1, 12345L, "{}", "VisualIdentity", 1, "pos diff", "neg", "https://cdn.project00.ai/ref.png");

        var diffNegative = DeterministicSeedDerivation.ComputeFingerprint(
            jobId, turnId, 1, 1, 12345L, "{}", "VisualIdentity", 1, "pos", "neg diff", "https://cdn.project00.ai/ref.png");

        var diffWorkflow = DeterministicSeedDerivation.ComputeFingerprint(
            jobId, turnId, 1, 1, 12345L, "{}", "OtherWorkflow", 1, "pos", "neg", "https://cdn.project00.ai/ref.png");

        var diffRef = DeterministicSeedDerivation.ComputeFingerprint(
            jobId, turnId, 1, 1, 12345L, "{}", "VisualIdentity", 1, "pos", "neg", "https://cdn.project00.ai/ref2.png");

        Assert.NotEqual(baseFp, diffPrompt);
        Assert.NotEqual(baseFp, diffNegative);
        Assert.NotEqual(baseFp, diffWorkflow);
        Assert.NotEqual(baseFp, diffRef);
    }
}
