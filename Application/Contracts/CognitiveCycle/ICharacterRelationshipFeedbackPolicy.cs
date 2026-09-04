using System;
using Domain.Enums;

namespace Application.Contracts.CognitiveCycle;

public sealed record CharacterRelationshipFeedbackProposal(
    int TrustDelta,
    int AffectionDelta,
    int FamiliarityDelta,
    RelationshipType? NewRelationshipType,
    string? Reason
);

/// <summary>
/// Domain/Application policy that evaluates cognitive cycle outcomes into explicit, structured relationship deltas.
/// Does NOT derive changes from free-form natural language heuristics.
/// </summary>
public interface ICharacterRelationshipFeedbackPolicy
{
    CharacterRelationshipFeedbackProposal? Evaluate(
        CharacterCognitiveCycleContext cycleContext,
        CharacterCognitiveCycleResult cycleResult);
}

/// <summary>
/// Default deterministic policy for evaluating relationship outcome deltas from a cognitive cycle.
/// </summary>
public sealed class DefaultCharacterRelationshipFeedbackPolicy : ICharacterRelationshipFeedbackPolicy
{
    public CharacterRelationshipFeedbackProposal? Evaluate(
        CharacterCognitiveCycleContext cycleContext,
        CharacterCognitiveCycleResult cycleResult)
    {
        ArgumentNullException.ThrowIfNull(cycleContext);
        ArgumentNullException.ThrowIfNull(cycleResult);

        // If cycle was invalid or not found, no feedback is produced
        if (cycleResult.Status is CharacterCognitiveCycleStatus.InvalidInput or CharacterCognitiveCycleStatus.NotFound)
        {
            return null;
        }

        // Only evaluate if target exists (e.g. UserMessage event)
        if (cycleContext.Event?.Target == null)
        {
            return null;
        }

        return cycleResult.Status switch
        {
            CharacterCognitiveCycleStatus.CompletedWithAction =>
                new CharacterRelationshipFeedbackProposal(
                    TrustDelta: 1,
                    AffectionDelta: 1,
                    FamiliarityDelta: 1,
                    NewRelationshipType: null,
                    Reason: "Completed actionable response to target message."
                ),

            CharacterCognitiveCycleStatus.AlreadyExecuted =>
                new CharacterRelationshipFeedbackProposal(
                    TrustDelta: 1,
                    AffectionDelta: 1,
                    FamiliarityDelta: 1,
                    NewRelationshipType: null,
                    Reason: "Completed actionable response to target message."
                ),

            CharacterCognitiveCycleStatus.CompletedWithoutAction =>
                new CharacterRelationshipFeedbackProposal(
                    TrustDelta: 0,
                    AffectionDelta: 0,
                    FamiliarityDelta: 1,
                    NewRelationshipType: null,
                    Reason: "Acknowledged interaction without overt action."
                ),

            // Infrastructure or identity failures (Failed, ConcurrencyConflict, IdempotencyConflict, NotFound, InvalidInput)
            // must NOT mutate social relationship metrics.
            _ => null
        };
    }
}
