using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Xunit;

namespace Tests.CharacterReaction;

public sealed class CharacterReactionPriorityTests
{
    [Fact]
    public void Priority_DirectUserMessageTakesPrecedenceOverAmbientAndSystemEvents()
    {
        var charId = Guid.NewGuid();
        var state = CharacterStateSnapshot.CreateDefault();

        var userEvent = CharacterWorldEvent.Create(charId, CharacterWorldEventType.UserMessage, "Chat", payloadJson: "Hello!");
        var userPerception = CharacterPerceptionPolicy.EvaluatePerception(userEvent, state);
        var userReaction = CharacterReactionPolicy.EvaluateReaction(userPerception, userEvent, state);

        var ambientEvent = CharacterWorldEvent.Create(charId, CharacterWorldEventType.ExternalWorldEvent, "World", payloadJson: "A gentle breeze blows.");
        var ambientPerception = CharacterPerceptionPolicy.EvaluatePerception(ambientEvent, state);
        var ambientReaction = CharacterReactionPolicy.EvaluateReaction(ambientPerception, ambientEvent, state);

        var systemEvent = CharacterWorldEvent.Create(charId, CharacterWorldEventType.SystemEvent, "System", payloadJson: "Routine health check");
        var systemPerception = CharacterPerceptionPolicy.EvaluatePerception(systemEvent, state);
        var systemReaction = CharacterReactionPolicy.EvaluateReaction(systemPerception, systemEvent, state);

        // Lower enum integer value indicates strictly higher priority
        Assert.True((int)userReaction.Priority < (int)ambientReaction.Priority);
        Assert.True((int)ambientReaction.Priority < (int)systemReaction.Priority);
        Assert.Equal(ReactionPriority.DirectUserInteraction, userReaction.Priority);
        Assert.Equal(ReactionPriority.AmbientWorld, ambientReaction.Priority);
        Assert.Equal(ReactionPriority.LowValueSystem, systemReaction.Priority);
    }
}
