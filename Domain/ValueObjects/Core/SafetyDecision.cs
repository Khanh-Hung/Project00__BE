using System;

namespace Domain.ValueObjects;

public sealed record SafetyDecision
{
    public bool IsAllowed { get; init; }
    public string PolicyCode { get; init; }
    public string? Reason { get; init; }

    public SafetyDecision(bool isAllowed, string policyCode, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(policyCode))
        {
            throw new ArgumentException("PolicyCode cannot be null or whitespace.", nameof(policyCode));
        }

        IsAllowed = isAllowed;
        PolicyCode = policyCode.Trim();
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }

    public static SafetyDecision Allowed(string policyCode = "ALLOWED", string? reason = null) =>
        new(true, policyCode, reason);

    public static SafetyDecision Denied(string policyCode, string reason) =>
        new(false, policyCode, reason);
}
