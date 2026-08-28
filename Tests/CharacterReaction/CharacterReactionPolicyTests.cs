using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Xunit;

namespace Tests.CharacterReaction;

public sealed class CharacterReactionPolicyTests
{
    [Fact]
    public void EvaluateReaction_PositiveUserMessage_CalculatesHappyStateDeltasAndVisualTrigger()
    {
        var charId = Guid.NewGuid();
        var evt = CharacterWorldEvent.Create(
            characterId: charId,
            eventType: CharacterWorldEventType.UserMessage,
            sourceType: "Chat",
            payloadJson: "Great work on completing the project!"
        );
        var state = CharacterStateSnapshot.CreateDefault();
        var perception = CharacterPerceptionPolicy.EvaluatePerception(evt, state);

        var reaction = CharacterReactionPolicy.EvaluateReaction(perception, evt, state);

        Assert.True(reaction.MoodDelta > 0);
        Assert.True(reaction.StressDelta < 0);
        Assert.True(reaction.ConfidenceDelta > 0);
        Assert.Equal(CharacterMood.Happy, reaction.NewMood);
        Assert.Equal(ReactionPriority.DirectUserInteraction, reaction.Priority);
        Assert.True(reaction.ShouldTriggerVisualMoment);
        Assert.NotNull(reaction.MemoryCandidate);
    }

    [Fact]
    public void EvaluateReaction_UrgentWarning_CalculatesAnxiousReactionAndActivityTrigger()
    {
        var charId = Guid.NewGuid();
        var evt = CharacterWorldEvent.Create(
            characterId: charId,
            eventType: CharacterWorldEventType.ExternalWorldEvent,
            sourceType: "World",
            payloadJson: "Disaster warning! Incoming storm attack!"
        );
        var state = CharacterStateSnapshot.CreateDefault();
        var perception = CharacterPerceptionPolicy.EvaluatePerception(evt, state);

        var reaction = CharacterReactionPolicy.EvaluateReaction(perception, evt, state);

        Assert.True(reaction.StressDelta > 0);
        Assert.Equal(CharacterMood.Anxious, reaction.NewMood);
        Assert.Equal(ReactionPriority.CriticalSurvival, reaction.Priority);
        Assert.True(reaction.ShouldTriggerActivity);
    }
}
