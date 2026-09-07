using System;
using System.Collections.Generic;

namespace Domain.Common;

/// <summary>
/// Authoritative trait keys for character personality dimensions.
/// Enforces canonical casing and validation across domain, persistence, and evidence layers.
/// </summary>
public static class PersonalityTraitKeys
{
    public const string Warmth = "Warmth";
    public const string Openness = "Openness";
    public const string Assertiveness = "Assertiveness";
    public const string Conscientiousness = "Conscientiousness";
    public const string SocialConfidence = "SocialConfidence";
    public const string TrustDisposition = "TrustDisposition";
    public const string EmotionalStability = "EmotionalStability";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Warmth,
        Openness,
        Assertiveness,
        Conscientiousness,
        SocialConfidence,
        TrustDisposition,
        EmotionalStability
    };

    public static bool IsValid(string? traitKey) =>
        !string.IsNullOrWhiteSpace(traitKey) && All.Contains(traitKey);

    public static string Normalize(string traitKey)
    {
        if (string.IsNullOrWhiteSpace(traitKey))
            throw new ArgumentException("TraitKey cannot be empty.", nameof(traitKey));

        foreach (var valid in All)
        {
            if (string.Equals(valid, traitKey, StringComparison.OrdinalIgnoreCase))
                return valid;
        }

        throw new ArgumentException($"Invalid personality trait key: '{traitKey}'. Supported keys: {string.Join(", ", All)}", nameof(traitKey));
    }
}
