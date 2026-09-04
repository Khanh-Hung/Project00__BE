using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Domain.ValueObjects;

namespace Domain.Common;

public static class CanonicalTransitionFingerprint
{
    public const int SchemaVersion = 1;

    public static string Compute(
        Guid characterId,
        Guid executionId,
        string sourceType,
        string? sourceId,
        CharacterStateDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        // Canonical deterministic payload with fixed field order and invariant formatting
        var canonical = string.Join(
            "|",
            SchemaVersion.ToString(CultureInfo.InvariantCulture),
            characterId.ToString("D"),
            executionId.ToString("D"),
            sourceType.Trim(),
            (sourceId ?? string.Empty).Trim(),
            delta.HungerDelta.ToString("F2", CultureInfo.InvariantCulture),
            delta.EnergyDelta.ToString("F2", CultureInfo.InvariantCulture),
            delta.MoodDelta.ToString("F2", CultureInfo.InvariantCulture),
            delta.StressDelta.ToString("F2", CultureInfo.InvariantCulture),
            delta.SocialNeedDelta.ToString("F2", CultureInfo.InvariantCulture),
            delta.ComfortDelta.ToString("F2", CultureInfo.InvariantCulture)
        );

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Creates the canonical source identifier representing a CharacterActionProposal's execution provenance.
    /// Format: ActionProposal:{Type}:{Intensity}:{SourceIntent}:{Motivation}:{StateVersion}
    /// Captures all semantic attributes of the proposal influencing execution.
    /// </summary>
    public static string CreateActionProposalSourceId(CharacterActionProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        return string.Join(
            ":",
            "ActionProposal",
            proposal.Type.ToString(),
            proposal.Intensity.ToString("F4", CultureInfo.InvariantCulture),
            proposal.SourceIntent.ToString(),
            proposal.Motivation.ToString(),
            proposal.StateVersion.ToString(CultureInfo.InvariantCulture)
        );
    }
}
