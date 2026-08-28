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
    private static readonly string[] UrgentKeywords = new[]
    {
        "danger", "warning", "help", "urgent", "attack", "emergency", "hazard", "threat",
        "khẩn cấp", "nguy hiểm", "cứu", "cháy", "bão", "tai nạn"
    };

    private static readonly string[] NegativeKeywords = new[]
    {
        "hate", "bad", "angry", "reject", "ugly", "disappoint", "useless", "fail", "stupid",
        "fool", "annoy", "worst", "liar", "ghét", "thất vọng", "tệ", "xấu", "vô dụng",
        "lừa dối", "chán", "bỏ rơi", "cút", "ngu"
    };

    private static readonly string[] PositiveKeywords = new[]
    {
        "love", "great", "good", "awesome", "beautiful", "thanks", "thank you", "proud",
        "amazing", "wonderful", "friend", "success", "successful", "magnificent", "excellent",
        "perfect", "brilliant", "splendid", "superb", "congratulations", "congrats", "joy",
        "happy", "khen", "yêu", "thích", "tốt", "đẹp", "tuyệt", "cảm ơn", "tự hào",
        "quý mến", "thành công", "chúc mừng", "xuất sắc"
    };

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
                (perceptionType, salience, valence, relevance, isRelevant, reason) = ClassifyUserMessage(worldEvent.PayloadJson, isSleeping);
                break;

            case CharacterWorldEventType.RelationshipChanged:
                salience = isSleeping ? EventSalience.Medium : EventSalience.High;
                relevance = 0.9f;
                isRelevant = true;
                perceptionType = PerceptionType.PositiveSocialFeedback;
                valence = EmotionalValence.Positive;
                if (!string.IsNullOrWhiteSpace(worldEvent.PayloadJson))
                {
                    var payloadLower = worldEvent.PayloadJson.ToLowerInvariant();
                    if (payloadLower.Contains("decay") || payloadLower.Contains("downgrade") ||
                        payloadLower.Contains("decrease") || payloadLower.Contains("break") ||
                        payloadLower.Contains("giảm") || payloadLower.Contains("rạn nứt"))
                    {
                        perceptionType = PerceptionType.NegativeSocialFeedback;
                        valence = EmotionalValence.Negative;
                    }
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
                reason = isSleeping ? "Activity completion ignored during sleep." : "Scheduled daily activity completed successfully.";
                break;

            case CharacterWorldEventType.SocialInteraction:
                if (!string.IsNullOrWhiteSpace(worldEvent.PayloadJson) && ContainsAny(worldEvent.PayloadJson, NegativeKeywords))
                {
                    perceptionType = PerceptionType.NegativeSocialFeedback;
                    salience = isSleeping ? EventSalience.Medium : EventSalience.High;
                    valence = EmotionalValence.Negative;
                    relevance = 0.85f;
                    isRelevant = !isSleeping;
                    reason = "Unfavorable social dispute occurred.";
                }
                else
                {
                    perceptionType = PerceptionType.PositiveSocialFeedback;
                    salience = isSleeping ? EventSalience.Medium : EventSalience.High;
                    valence = EmotionalValence.Positive;
                    relevance = 0.85f;
                    isRelevant = !isSleeping;
                    reason = "Social encounter occurred in shared environment.";
                }
                break;

            case CharacterWorldEventType.ExternalWorldEvent:
                if (!string.IsNullOrWhiteSpace(worldEvent.PayloadJson) &&
                    (ContainsAny(worldEvent.PayloadJson, UrgentKeywords) ||
                     worldEvent.PayloadJson.Contains("disaster", StringComparison.OrdinalIgnoreCase)))
                {
                    perceptionType = PerceptionType.UrgentWarning;
                    salience = EventSalience.Critical;
                    valence = EmotionalValence.Negative;
                    relevance = 1.0f;
                    isRelevant = true;
                    reason = "Critical external world hazard perceived (wakes character if asleep).";
                }
                else
                {
                    perceptionType = PerceptionType.EnvironmentalChange;
                    salience = isSleeping ? EventSalience.Low : EventSalience.Medium;
                    valence = EmotionalValence.Neutral;
                    relevance = isSleeping ? 0.0f : 0.5f;
                    isRelevant = !isSleeping;
                    reason = isSleeping ? "Environmental shift unnoticed during deep rest." : "Atmospheric or environmental shift noticed in surroundings.";
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

    private static (PerceptionType type, EventSalience salience, EmotionalValence valence, float relevance, bool isRelevant, string reason) ClassifyUserMessage(
        string? payload,
        bool isSleeping)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return (
                PerceptionType.PositiveSocialFeedback,
                isSleeping ? EventSalience.Low : EventSalience.Medium,
                EmotionalValence.Neutral,
                isSleeping ? 0.0f : 0.5f,
                !isSleeping,
                isSleeping ? "Empty message ignored while sleeping." : "Empty or neutral communication received."
            );
        }

        // 1. Check Urgent/Hazard cues
        if (ContainsAny(payload, UrgentKeywords))
        {
            return (
                PerceptionType.UrgentWarning,
                EventSalience.Critical,
                EmotionalValence.Negative,
                1.0f,
                true,
                "Direct urgent user message demanding immediate attention (wakes sleeping character)."
            );
        }

        // 2. Check Negative/Criticism/Insult cues
        if (ContainsAny(payload, NegativeKeywords))
        {
            return (
                PerceptionType.NegativeSocialFeedback,
                isSleeping ? EventSalience.High : EventSalience.Critical,
                EmotionalValence.Negative,
                0.95f,
                true,
                "Direct critical or hurtful user feedback perceived."
            );
        }

        // 3. Check Positive/Praise/Affection cues
        if (ContainsAny(payload, PositiveKeywords))
        {
            return (
                PerceptionType.PositiveSocialFeedback,
                isSleeping ? EventSalience.High : EventSalience.Critical,
                EmotionalValence.Positive,
                1.0f,
                true,
                "Warm positive user interaction received by character."
            );
        }

        // 4. Neutral / casual chatter
        return (
            PerceptionType.PositiveSocialFeedback,
            isSleeping ? EventSalience.Low : EventSalience.Medium,
            EmotionalValence.Neutral,
            isSleeping ? 0.0f : 0.8f,
            !isSleeping,
            isSleeping ? "Casual conversation unnoticed during sleep." : "Direct conversational user interaction."
        );
    }

    private static bool ContainsAny(string text, string[] keywords)
    {
        var lower = text.ToLowerInvariant();
        for (int i = 0; i < keywords.Length; i++)
        {
            if (lower.Contains(keywords[i]))
            {
                return true;
            }
        }
        return false;
    }
}
