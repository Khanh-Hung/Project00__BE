using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Xunit;

namespace Tests.CharacterReaction;

public sealed class CharacterPerceptionTests
{
    [Theory]
    [InlineData("Your painting is exquisite, I am so proud of you!")]
    [InlineData("Great job on completing the milestone!")]
    [InlineData("Chúc mừng bạn đã đạt được thành công rực rỡ!")]
    [InlineData("Cảm ơn bạn rất nhiều, bạn tuyệt vời lắm!")]
    public void EvaluatePerception_PositivePraiseUserMessage_YieldsPositiveSocialFeedbackAndHighSalience(string messageText)
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
    [InlineData("Đồ vô dụng, mày thật là tệ hại!")]
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
    [InlineData("Emergency threat detected nearby!")]
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

    [Theory]
    [InlineData("What time is it in the city?")]
    [InlineData("Tell me more about the library archives.")]
    [InlineData("Thời tiết ở xưởng vẽ hôm nay thế nào?")]
    public void EvaluatePerception_NeutralCasualUserMessage_YieldsNeutralValenceAndModerateSalience(string messageText)
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

        Assert.Equal(PerceptionType.PositiveSocialFeedback, perception.PerceptionType);
        Assert.Equal(EventSalience.Medium, perception.Salience);
        Assert.Equal(EmotionalValence.Neutral, perception.EmotionalValence);
        Assert.True(perception.IsRelevant);
        Assert.Equal(0.8f, perception.Relevance);
    }

    [Fact]
    public void EvaluatePerception_SleepingCharacter_IgnoresNeutralUserMessage()
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

        var evt = CharacterWorldEvent.Create(
            characterId: charId,
            eventType: CharacterWorldEventType.UserMessage,
            sourceType: "Chat",
            payloadJson: "What time is it?"
        );

        var perception = CharacterPerceptionPolicy.EvaluatePerception(evt, state, currentActivity: sleepingActivity);

        Assert.False(perception.IsRelevant);
        Assert.Equal(EventSalience.Low, perception.Salience);
    }

    [Fact]
    public void EvaluatePerception_SleepingCharacter_AwakenedByUrgentUserMessage()
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

        var evt = CharacterWorldEvent.Create(
            characterId: charId,
            eventType: CharacterWorldEventType.UserMessage,
            sourceType: "Chat",
            payloadJson: "Emergency! Danger, wake up now!"
        );

        var perception = CharacterPerceptionPolicy.EvaluatePerception(evt, state, currentActivity: sleepingActivity);

        Assert.True(perception.IsRelevant);
        Assert.Equal(PerceptionType.UrgentWarning, perception.PerceptionType);
        Assert.Equal(EventSalience.Critical, perception.Salience);
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

    [Fact]
    public void EvaluatePerception_KnownLimitation_DeterministicRuleBasedHeuristicEvaluatesKeywordsDeterministically()
    {
        // NOTE: CharacterPerceptionPolicy is a pure, zero-LLM deterministic heuristic classifier.
        // It provides reproducible domain boundaries. Semantic negation phrases like "I don't hate you"
        // trigger the negative keyword "hate" by design in this rule-based baseline until an optional LLM enricher runs downstream.
        var charId = Guid.NewGuid();
        var evt = CharacterWorldEvent.Create(
            characterId: charId,
            eventType: CharacterWorldEventType.UserMessage,
            sourceType: "Chat",
            payloadJson: "I don't hate you"
        );
        var state = CharacterStateSnapshot.CreateDefault();

        var perception = CharacterPerceptionPolicy.EvaluatePerception(evt, state);

        Assert.Equal(PerceptionType.NegativeSocialFeedback, perception.PerceptionType);
        Assert.Equal(EmotionalValence.Negative, perception.EmotionalValence);
    }
}
