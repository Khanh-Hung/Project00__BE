using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Xunit;

namespace Tests.CharacterReaction;

public sealed class CharacterPerceptionTests
{
    [Fact]
    public void EvaluatePerception_DirectUserMessage_YieldsCriticalSalienceAndHighRelevance()
    {
        var charId = Guid.NewGuid();
        var evt = CharacterWorldEvent.Create(
            characterId: charId,
            eventType: CharacterWorldEventType.UserMessage,
            sourceType: "Chat",
            payloadJson: "{\"text\":\"Your painting is exquisite!\"}"
        );
        var state = CharacterStateSnapshot.CreateDefault();

        var perception = CharacterPerceptionPolicy.EvaluatePerception(evt, state);

        Assert.Equal(charId, perception.CharacterId);
        Assert.Equal(evt.Id, perception.WorldEventId);
        Assert.Equal(PerceptionType.PositiveSocialFeedback, perception.PerceptionType);
        Assert.Equal(EventSalience.Critical, perception.Salience);
        Assert.Equal(EmotionalValence.Positive, perception.EmotionalValence);
        Assert.True(perception.IsRelevant);
        Assert.Equal(1.0f, perception.Relevance);
    }

    [Fact]
    public void EvaluatePerception_UrgentHazardUserMessage_YieldsUrgentWarning()
    {
        var charId = Guid.NewGuid();
        var evt = CharacterWorldEvent.Create(
            characterId: charId,
            eventType: CharacterWorldEventType.UserMessage,
            sourceType: "Chat",
            payloadJson: "{\"text\":\"Danger! An attack is incoming, get help immediately!\"}"
        );
        var state = CharacterStateSnapshot.CreateDefault();

        var perception = CharacterPerceptionPolicy.EvaluatePerception(evt, state);

        Assert.Equal(PerceptionType.UrgentWarning, perception.PerceptionType);
        Assert.Equal(EventSalience.Critical, perception.Salience);
        Assert.Equal(EmotionalValence.Negative, perception.EmotionalValence);
        Assert.True(perception.IsRelevant);
    }

    [Fact]
    public void EvaluatePerception_SleepingCharacter_FiltersOutLowPrioritySystemEvent()
    {
        var charId = Guid.NewGuid();
        var evt = CharacterWorldEvent.Create(
            characterId: charId,
            eventType: CharacterWorldEventType.SystemEvent,
            sourceType: "BackgroundDaemon",
            payloadJson: "{\"event\":\"Routine sync\"}"
        );
        var sleepingActivity = new CharacterActivity(
            characterId: charId,
            activityType: CharacterActivityType.Sleeping,
            location: "Bedroom",
            timeBucket: "202608280200",
            decisionFingerprint: "sleep-001",
            source: CharacterActivitySource.Autonomous,
            priority: ActivityPriority.High,
            durationMinutes: 480,
            shouldCreateVisualMoment: false,
            reason: "Sleeping",
            startedAt: DateTime.UtcNow
        );
        var state = new CharacterStateSnapshot(energy: 10);

        var perception = CharacterPerceptionPolicy.EvaluatePerception(
            worldEvent: evt,
            state: state,
            currentActivity: sleepingActivity
        );

        Assert.False(perception.IsRelevant);
        Assert.Equal(EventSalience.Low, perception.Salience);
    }
}
