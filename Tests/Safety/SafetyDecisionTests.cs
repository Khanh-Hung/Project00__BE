using System;
using Domain.ValueObjects;
using Xunit;

namespace Tests.Safety;

public sealed class SafetyDecisionTests
{
    [Fact]
    public void Allowed_WithDefaults_SetsExpectedProperties()
    {
        var decision = SafetyDecision.Allowed();

        Assert.True(decision.IsAllowed);
        Assert.Equal("ALLOWED", decision.PolicyCode);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void Allowed_WithCustomCodeAndReason_SetsProperties()
    {
        var decision = SafetyDecision.Allowed("CUSTOM_ALLOWED", "Approved by policy");

        Assert.True(decision.IsAllowed);
        Assert.Equal("CUSTOM_ALLOWED", decision.PolicyCode);
        Assert.Equal("Approved by policy", decision.Reason);
    }

    [Fact]
    public void Denied_SetsExpectedProperties()
    {
        var decision = SafetyDecision.Denied("POLICY_BLOCKED", "Harmful action detected");

        Assert.False(decision.IsAllowed);
        Assert.Equal("POLICY_BLOCKED", decision.PolicyCode);
        Assert.Equal("Harmful action detected", decision.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ThrowsArgumentException_WhenPolicyCodeIsNullOrWhitespace(string? invalidCode)
    {
        Assert.Throws<ArgumentException>(() => new SafetyDecision(true, invalidCode!));
    }

    [Fact]
    public void ValueEquality_TwoIdenticalDecisions_AreEqual()
    {
        var d1 = SafetyDecision.Denied("TEST", "reason");
        var d2 = SafetyDecision.Denied("TEST", "reason");

        Assert.Equal(d1, d2);
        Assert.True(d1 == d2);
    }

    [Fact]
    public void ValueEquality_DifferentDecisions_AreNotEqual()
    {
        var d1 = SafetyDecision.Allowed("ALLOWED");
        var d2 = SafetyDecision.Denied("BLOCKED", "reason");

        Assert.NotEqual(d1, d2);
    }
}
