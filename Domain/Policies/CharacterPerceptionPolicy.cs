using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Policies;

/// <summary>
/// Pure, deterministic domain policy that converts world events and character context into a CharacterPerception.
/// Zero database access, zero LLM dependencies, 100% reproducible.
/// </summary>
public static class CharacterPerceptionPolicy
{
    public static CharacterPerception EvaluatePerception(
        CharacterWorldEvent worldEvent,
        CharacterStateSnapshot state,
        IReadOnlyList<GoalSnapshot>? goals = null,
        CharacterActivity? currentActivity = null,
        CharacterVisualState? currentVisualState = null)
    {
        ArgumentNullException.ThrowIfNull(worldEvent, nameof(worldEvent));
        ArgumentNullException.ThrowIfNull(state, nameof(state));

        bool isSleeping = currentActivity?.ActivityType == CharacterActivityType.Sleeping || state.Energy < 15;

        PerceptionType perceptionType;
        EventSalience salience;
        EmotionalValence valence;
        float relevance;
        bool isRelevant;
        string reason;

        switch (worldEvent.EventType)
        {
            case CharacterWorldEventType.UserMessage:
                perceptionType = PerceptionType.PositiveSocialFeedback;
                if (!string.IsNullOrWhiteSpace(worldEvent.PayloadJson))
                {
                    var payloadLower = worldEvent.PayloadJson.ToLowerInvariant();
                    if (payloadLower.Contains("danger") || payloadLower.Contains("warning") || payloadLower.Contains("help") || payloadLower.Contains("urgent"))
                    {
                        perceptionType = PerceptionType.UrgentWarning;
                        salience = EventSalience.Critical;
                        valence = EmotionalValence.Negative;
                        relevance = 1.0f;
                        isRelevant = true;
                        reason = "Direct urgent user message demanding immediate attention.";
                        break;
                    }
                    if (payloadLower.Contains("bad") || payloadLower.Contains("angry") || payloadLower.Contains("hate") || payloadLower.Contains("reject") || payloadLower.Contains("ugly"))
                    {
                        perceptionType = PerceptionType.NegativeSocialFeedback;
                        salience = EventSalience.High;
                        valence = EmotionalValence.Negative;
                        relevance = 0.95f;
                        isRelevant = true;
                        reason = "Direct critical user feedback perceived.";
                        break;
                    }
                }

                salience = isSleeping ? EventSalience.High : EventSalience.Critical;
                valence = EmotionalValence.Positive;
                relevance = 1.0f;
                isRelevant = true;
                reason = "Direct user interaction received by character.";
                break;

            case CharacterWorldEventType.RelationshipChanged:
                salience = EventSalience.High;
                relevance = 0.9f;
                isRelevant = true;
                perceptionType = PerceptionType.PositiveSocialFeedback;
                valence = EmotionalValence.Positive;
                if (!string.IsNullOrWhiteSpace(worldEvent.PayloadJson) && worldEvent.PayloadJson.Contains("decay", StringComparison.OrdinalIgnoreCase))
                {
                    perceptionType = PerceptionType.NegativeSocialFeedback;
                    valence = EmotionalValence.Negative;
                }
                reason = "Interpersonal relationship shift perceived in social circle.";
                break;

            case CharacterWorldEventType.GoalProgressed:
            case CharacterWorldEventType.GoalCompleted:
                perceptionType = PerceptionType.GoalMilestoneReached;
                salience = worldEvent.EventType == CharacterWorldEventType.GoalCompleted ? EventSalience.Critical : EventSalience.High;
                valence = EmotionalValence.Positive;
                relevance = 1.0f;
                isRelevant = true;
                reason = worldEvent.EventType == CharacterWorldEventType.GoalCompleted
                    ? "Long-term personal ambition successfully completed!"
                    : "Measurable progress recorded toward active life goal.";
                break;

            case CharacterWorldEventType.ActivityCompleted:
                perceptionType = PerceptionType.RoutineActivityOutcome;
                salience = EventSalience.Medium;
                valence = EmotionalValence.Positive;
                relevance = 0.7f;
                isRelevant = !isSleeping;
                reason = "Scheduled daily activity completed successfully.";
                break;

            case CharacterWorldEventType.SocialInteraction:
                perceptionType = PerceptionType.PositiveSocialFeedback;
                salience = EventSalience.High;
                valence = EmotionalValence.Positive;
                relevance = 0.85f;
                isRelevant = true;
                reason = "Social encounter occurred in shared environment.";
                break;

            case CharacterWorldEventType.ExternalWorldEvent:
                if (!string.IsNullOrWhiteSpace(worldEvent.PayloadJson) &&
                    (worldEvent.PayloadJson.Contains("disaster", StringComparison.OrdinalIgnoreCase) ||
                     worldEvent.PayloadJson.Contains("danger", StringComparison.OrdinalIgnoreCase) ||
                     worldEvent.PayloadJson.Contains("attack", StringComparison.OrdinalIgnoreCase)))
                {
                    perceptionType = PerceptionType.UrgentWarning;
                    salience = EventSalience.Critical;
                    valence = EmotionalValence.Negative;
                    relevance = 1.0f;
                    isRelevant = true;
                    reason = "Critical external world hazard perceived.";
                }
                else
                {
                    perceptionType = PerceptionType.EnvironmentalChange;
                    salience = isSleeping ? EventSalience.Low : EventSalience.Medium;
                    valence = EmotionalValence.Neutral;
                    relevance = isSleeping ? 0.1f : 0.5f;
                    isRelevant = !isSleeping;
                    reason = "Atmospheric or environmental shift noticed in surroundings.";
                }
                break;

            case CharacterWorldEventType.NewLocation:
                perceptionType = PerceptionType.EnvironmentalChange;
                salience = EventSalience.Medium;
                valence = EmotionalValence.Neutral;
                relevance = 0.6f;
                isRelevant = true;
                reason = "Character transitioned to a new geographic location.";
                break;

            case CharacterWorldEventType.SystemEvent:
            default:
                perceptionType = PerceptionType.SystemNotice;
                salience = EventSalience.Low;
                valence = EmotionalValence.Neutral;
                relevance = 0.2f;
                isRelevant = !isSleeping;
                reason = "Routine system observation recorded.";
                break;
        }

        return new CharacterPerception(
            CharacterId: worldEvent.CharacterId,
            WorldEventId: worldEvent.Id,
            PerceptionType: perceptionType,
            Salience: salience,
            EmotionalValence: valence,
            Relevance: relevance,
            IsRelevant: isRelevant,
            Reason: reason
        );
    }
}
