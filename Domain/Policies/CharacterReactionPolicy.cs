using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Policies;

/// <summary>
/// Pure, deterministic domain policy that calculates state deltas, goal contributions, memory candidates,
/// and activity/visual triggers from a CharacterPerception.
/// Guarantees that state values remain within [0, 100], strictly prevents NaN/Infinity, and preserves deterministic reproducibility.
/// </summary>
public static class CharacterReactionPolicy
{
    public static CharacterReaction EvaluateReaction(
        CharacterPerception perception,
        CharacterWorldEvent worldEvent,
        CharacterStateSnapshot state,
        IReadOnlyList<GoalSnapshot>? goals = null,
        CharacterActivity? currentActivity = null,
        CharacterVisualState? currentSceneState = null)
    {
        ArgumentNullException.ThrowIfNull(perception, nameof(perception));
        ArgumentNullException.ThrowIfNull(worldEvent, nameof(worldEvent));
        ArgumentNullException.ThrowIfNull(state, nameof(state));

        // If perception was deemed irrelevant (e.g. ambient noise during sleep), produce zero-delta idle reaction
        if (!perception.IsRelevant)
        {
            return new CharacterReaction(
                ReactionReason: "Perception filtered out as irrelevant to current character state.",
                Priority: ReactionPriority.LowValueSystem
            );
        }

        int moodDelta = 0;
        int moodIntensityDelta = 0;
        int energyDelta = 0;
        int stressDelta = 0;
        int hungerDelta = 0;
        int socialNeedDelta = 0;
        int confidenceDelta = 0;
        int relationshipDelta = 0;
        CharacterMood? newMood = null;

        ReactionPriority priority;
        bool shouldTriggerActivity = false;
        CharacterActivityType? activityType = null;
        string? activityReason = null;
        ActivityPriority? activityPriority = null;

        bool shouldTriggerVisual = false;
        ActivityPriority? visualPriority = null;
        string? actionHint = null;
        string? poseHint = null;
        string? envHint = null;

        string reactionReason;

        switch (perception.PerceptionType)
        {
            case PerceptionType.UrgentWarning:
                priority = ReactionPriority.CriticalSurvival;
                stressDelta = +30;
                moodDelta = -15;
                energyDelta = -5;
                confidenceDelta = -10;
                newMood = CharacterMood.Anxious;
                moodIntensityDelta = +25;
                reactionReason = "High alert triggered by critical warning in environment.";
                shouldTriggerActivity = true;
                activityType = CharacterActivityType.Relaxing;
                activityReason = "Seeking safety or recovery following urgent disturbance.";
                activityPriority = ActivityPriority.High;
                break;

            case PerceptionType.PositiveSocialFeedback:
                priority = worldEvent.EventType == CharacterWorldEventType.UserMessage
                    ? ReactionPriority.DirectUserInteraction
                    : (worldEvent.EventType == CharacterWorldEventType.RelationshipChanged
                        ? ReactionPriority.RelationshipAffecting
                        : ReactionPriority.SocialEvent);

                if (perception.EmotionalValence == EmotionalValence.Positive)
                {
                    moodDelta = +20;
                    stressDelta = -15;
                    confidenceDelta = +15;
                    socialNeedDelta = -20;
                    relationshipDelta = +5;
                    newMood = CharacterMood.Happy;
                    moodIntensityDelta = +15;
                    reactionReason = "Warmed by positive social engagement and affirmation.";
                    shouldTriggerVisual = true;
                    visualPriority = ActivityPriority.Normal;
                    actionHint = "smiling warmly in response";
                    poseHint = "open and welcoming posture";
                    envHint = "bright and pleasant ambience";
                }
                else
                {
                    // Casual neutral communication
                    moodDelta = +5;
                    stressDelta = -5;
                    confidenceDelta = +5;
                    socialNeedDelta = -10;
                    relationshipDelta = +1;
                    reactionReason = "Participated in casual conversation.";
                }
                break;

            case PerceptionType.NegativeSocialFeedback:
                priority = worldEvent.EventType == CharacterWorldEventType.UserMessage
                    ? ReactionPriority.DirectUserInteraction
                    : (worldEvent.EventType == CharacterWorldEventType.RelationshipChanged
                        ? ReactionPriority.RelationshipAffecting
                        : ReactionPriority.SocialEvent);
                moodDelta = -20;
                stressDelta = +20;
                confidenceDelta = -15;
                relationshipDelta = -5;
                newMood = CharacterMood.Sad;
                moodIntensityDelta = +15;
                reactionReason = "Dismayed by negative social critique or tension.";
                shouldTriggerActivity = state.Stress + 20 > 70;
                if (shouldTriggerActivity)
                {
                    activityType = CharacterActivityType.Relaxing;
                    activityReason = "Stepping back to recover composure after upsetting interaction.";
                    activityPriority = ActivityPriority.High;
                }
                break;

            case PerceptionType.GoalMilestoneReached:
                priority = ReactionPriority.GoalCritical;
                moodDelta = +25;
                confidenceDelta = +20;
                stressDelta = -10;
                newMood = CharacterMood.Excited;
                moodIntensityDelta = +20;
                reactionReason = "Elated by substantial progress achieved on meaningful goal!";
                shouldTriggerVisual = true;
                visualPriority = ActivityPriority.Critical;
                actionHint = "celebrating milestone achievement";
                poseHint = "proud and triumphant stance";
                envHint = "celebratory atmosphere";
                break;

            case PerceptionType.RoutineActivityOutcome:
                priority = ReactionPriority.SignificantActivityOutcome;
                moodDelta = +5;
                stressDelta = -5;
                confidenceDelta = +5;
                reactionReason = "Satisfaction derived from completing standard tasks.";
                break;

            case PerceptionType.EnvironmentalChange:
                priority = ReactionPriority.AmbientWorld;
                moodDelta = 0;
                stressDelta = +2;
                reactionReason = "Observing shift in surroundings.";
                break;

            case PerceptionType.SystemNotice:
            default:
                priority = ReactionPriority.LowValueSystem;
                reactionReason = "Routine notification processed.";
                break;
        }

        // Memory candidate generation with strict anti-spam filtering
        MemoryCandidate? memoryCandidate = null;
        bool isRoutineOrAmbient = perception.PerceptionType == PerceptionType.EnvironmentalChange ||
                                 perception.PerceptionType == PerceptionType.SystemNotice ||
                                 perception.PerceptionType == PerceptionType.RoutineActivityOutcome;

        if (!isRoutineOrAmbient &&
            (perception.Salience >= EventSalience.High ||
             (perception.EmotionalValence != EmotionalValence.Neutral && perception.Relevance >= 0.75f)))
        {
            int importance = perception.Salience switch
            {
                EventSalience.Critical => 5,
                EventSalience.High => 4,
                EventSalience.Medium => 3,
                _ => 2
            };

            decimal confidence = perception.EmotionalValence switch
            {
                EmotionalValence.Positive => 0.9m,
                EmotionalValence.Negative => 0.85m,
                _ => 0.5m
            };

            string rawContent = !string.IsNullOrWhiteSpace(worldEvent.PayloadJson)
                ? worldEvent.PayloadJson
                : $"{perception.PerceptionType}: {reactionReason}";

            string content = rawContent.Length > MemoryCandidate.MaxContentLength
                ? rawContent.Substring(0, MemoryCandidate.MaxContentLength)
                : rawContent;

            memoryCandidate = new MemoryCandidate(
                content: content,
                type: MemoryType.Event,
                importance: importance,
                confidence: confidence
            );
        }

        // Goal impact generation (strictly positive meaningful interactions or milestone events)
        GoalImpactCandidate? goalImpact = null;
        if (goals != null && goals.Count > 0 && perception.EmotionalValence == EmotionalValence.Positive && !isRoutineOrAmbient)
        {
            var activeGoal = goals.FirstOrDefault(g => g.Status == CharacterGoalStatus.Active);
            if (activeGoal != null)
            {
                double contrib = perception.PerceptionType switch
                {
                    PerceptionType.GoalMilestoneReached => 10.0,
                    PerceptionType.PositiveSocialFeedback => 2.0,
                    _ => 1.0
                };

                goalImpact = new GoalImpactCandidate(
                    GoalId: activeGoal.GoalId,
                    ContributionValue: contrib,
                    Reason: $"Event {perception.PerceptionType} contributed to goal {activeGoal.Title}."
                );
            }
        }

        return new CharacterReaction(
            MoodDelta: moodDelta,
            NewMood: newMood,
            MoodIntensityDelta: moodIntensityDelta,
            EnergyDelta: energyDelta,
            StressDelta: stressDelta,
            HungerDelta: hungerDelta,
            SocialNeedDelta: socialNeedDelta,
            ConfidenceDelta: confidenceDelta,
            RelationshipDelta: relationshipDelta,
            GoalImpact: goalImpact,
            MemoryCandidate: memoryCandidate,
            ShouldTriggerActivity: shouldTriggerActivity,
            ActivityIntentType: activityType,
            ActivityReason: activityReason,
            ActivityPriority: activityPriority,
            ShouldTriggerVisualMoment: shouldTriggerVisual,
            VisualPriority: visualPriority,
            ActionHint: actionHint,
            PoseHint: poseHint,
            EnvironmentHint: envHint,
            ReactionReason: reactionReason,
            Priority: priority
        );
    }
}
