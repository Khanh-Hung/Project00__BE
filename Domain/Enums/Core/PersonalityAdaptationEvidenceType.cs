namespace Domain.Enums;

/// <summary>
/// Strongly typed categories of behavioral evidence that can drive long-term personality adaptation.
/// Invariant: Must only include categories reliably derivable from authoritative cognitive cycle outcomes.
/// </summary>
public enum PersonalityAdaptationEvidenceType
{
    PositiveSocialOutcome = 1,
    NegativeSocialOutcome = 2,
    RepeatedSuccessfulInteraction = 3,
    RepeatedConflict = 4,
    EmotionalRegulation = 5,
    BehavioralConsistency = 6
}
