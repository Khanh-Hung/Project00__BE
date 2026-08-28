using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Xunit;

namespace Tests.CharacterReaction;

public sealed class CharacterPerceptionTests
{
    [Fact]
    public void EvaluatePerception_PositivePraiseUserMessage_YieldsPositiveSocialFeedbackAndHighSalience()
    {
        var charId = Guid.NewGuid();
        var evt = CharacterWorldEvent.Create(
            characterId: charId,
            eventType: CharacterWorldEventType.UserMessage,
            sourceType: "Chat",
            payloadJson: "Your painting is exquisite, I am so proud of you!"
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

    [Theory]
    [InlineData("Tao ghét mày.")]
    [InlineData("Tôi thất vọng về cô.")]
    [InlineData("You are completely useless and failed me.")]
    [InlineData("You are ugly and stupid.")]
    public void EvaluatePerception_NegativeHostileUserMessage_YieldsNegativeSocialFeedback(string messageText)
    {
        var charId = Guid.NewGuid();
        var evt = CharacterWorldEvent.Create(
            characterId: charId,
            eventType: CharacterWorldEventType.UserMessage,
            sourceType: "Chat",
            payloadJson: messageText
        );
        var state = CharacterStateSnapshot.CreateDefault();

        var perception = CharacterPerceptionPolicy.EvaluatePerception(evt, state);

        Assert.Equal(PerceptionType.NegativeSocialFeedback, perception.PerceptionType);
        Assert.Equal(EventSalience.Critical, perception.Salience);
        Assert.Equal(EmotionalValence.Negative, perception.EmotionalValence);
        Assert.True(perception.IsRelevant);
        Assert.Equal(0.95f, perception.Relevance);
    }

    [Theory]
    [InlineData("Danger! An attack is incoming, get help immediately!")]
    [InlineData("Nguy hiểm! Có cháy khẩn cấp, hãy chạy ngay!")]
    public void EvaluatePerception_UrgentHazardUserMessage_YieldsUrgentWarning(string urgentText)
    {
        var charId = Guid.NewGuid();
        var evt = CharacterWorldEvent.Create(
            characterId: charId,
            eventType: CharacterWorldEventType.UserMessage,
            sourceType: "Chat",
            payloadJson: urgentText
        );
        var state = CharacterStateSnapshot.CreateDefault();

        var perception = CharacterPerceptionPolicy.EvaluatePerception(evt, state);

        Assert.Equal(PerceptionType.UrgentWarning, perception.PerceptionType);
        Assert.Equal(EventSalience.Critical, perception.Salience);
        Assert.Equal(EmotionalValence.Negative, perception.EmotionalValence);
        Assert.True(perception.IsRelevant);
        Assert.Equal(1.0f, perception.Relevance);
    }

    [Fact]
    public void EvaluatePerception_SleepingCharacter_FiltersOutAmbientWeatherAndSystemEvents()
    {
        var charId = Guid.NewGuid();
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

        // 1. Ambient rain event during sleep -> Filtered out
        var rainEvent = CharacterWorldEvent.Create(
            characterId: charId,
            eventType: CharacterWorldEventType.ExternalWorldEvent,
            sourceType: "Weather",
            payloadJson: "Rain started falling lightly outside."
        );
        var rainPerception = CharacterPerceptionPolicy.EvaluatePerception(rainEvent, state, currentActivity: sleepingActivity);
        Assert.False(rainPerception.IsRelevant);
        Assert.Equal(EventSalience.Low, rainPerception.Salience);

        // 2. Routine system sync during sleep -> Filtered out
        var sysEvent = CharacterWorldEvent.Create(
            characterId: charId,
            eventType: CharacterWorldEventType.SystemEvent,
            sourceType: "SystemDaemon",
            payloadJson: "Background garbage collection complete."
        );
        var sysPerception = CharacterPerceptionPolicy.EvaluatePerception(sysEvent, state, currentActivity: sleepingActivity);
        Assert.False(sysPerception.IsRelevant);
        Assert.Equal(EventSalience.Low, sysPerception.Salience);
    }

    [Fact]
    public void EvaluatePerception_SleepingCharacter_AwakenedByCriticalWorldHazard()
    {
        var charId = Guid.NewGuid();
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

        var hazardEvent = CharacterWorldEvent.Create(
            characterId: charId,
            eventType: CharacterWorldEventType.ExternalWorldEvent,
            sourceType: "Environment",
            payloadJson: "Disaster warning! Toxic gas leak detected in residential sector!"
        );

        var perception = CharacterPerceptionPolicy.EvaluatePerception(hazardEvent, state, currentActivity: sleepingActivity);

        Assert.True(perception.IsRelevant);
        Assert.Equal(PerceptionType.UrgentWarning, perception.PerceptionType);
        Assert.Equal(EventSalience.Critical, perception.Salience);
        Assert.Equal(EmotionalValence.Negative, perception.EmotionalValence);
    }
}
