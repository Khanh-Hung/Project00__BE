using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Policies;

/// <summary>
/// Pure, deterministic domain policy that converts world events and character context into a CharacterPerception.
/// Zero database access, zero LLM dependencies, 100% reproducible.
/// </summary>
public sealed class CharacterPerceptionPolicy : ICharacterPerceptionPolicy
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

    #region PR39: Character Perception & Internal Experience

    /// <summary>
    /// Pure, deterministic domain policy that converts authoritative CharacterStateSnapshot and CharacterPsychologyProfile
    /// into an immutable CharacterInternalExperience without state mutation or side effects.
    /// </summary>
    public CharacterInternalExperience Evaluate(
        CharacterStateSnapshot state,
        PsychologyProfile? psychology = null,
        CharacterPerceptionContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(state, nameof(state));

        // 1. Validate numeric inputs
        ValidateMetric(state.Hunger, nameof(state.Hunger));
        ValidateMetric(state.Energy, nameof(state.Energy));
        ValidateMetric(state.MoodScalar, nameof(state.MoodScalar));
        ValidateMetric(state.Stress, nameof(state.Stress));
        ValidateMetric(state.SocialNeed, nameof(state.SocialNeed));
        ValidateMetric(state.Comfort, nameof(state.Comfort));

        var psych = psychology ?? PsychologyProfile.Default;
        ValidateSensitivity(psych.HungerSensitivity, nameof(psych.HungerSensitivity));
        ValidateSensitivity(psych.FatigueSensitivity, nameof(psych.FatigueSensitivity));
        ValidateSensitivity(psych.StressSensitivity, nameof(psych.StressSensitivity));
        ValidateSensitivity(psych.SocialSensitivity, nameof(psych.SocialSensitivity));
        ValidateSensitivity(psych.ComfortSensitivity, nameof(psych.ComfortSensitivity));
        ValidateSensitivity(psych.MoodReactivity, nameof(psych.MoodReactivity));

        var evaluatedAtUtc = context?.EvaluatedAtUtc ?? state.LastEvolvedAtUtc ?? DateTime.UnixEpoch;
        var characterId = context?.CharacterId ?? Guid.Empty;

        // 2. Discretize Levels
        var hungerLevel = DiscretizeHunger(state.Hunger);
        var energyLevel = DiscretizeEnergy(state.Energy);
        var stressLevel = DiscretizeStress(state.Stress);
        var socialNeedLevel = DiscretizeSocialNeed(state.SocialNeed);
        var comfortLevel = DiscretizeComfort(state.Comfort);
        var moodLevel = DiscretizeMood(state.MoodScalar);

        // 3. Calculate Subjective Intensities
        var hungerIntensity = CalculateIntensity(state.Hunger, psych.HungerSensitivity);
        var energyIntensity = CalculateIntensity(state.Energy, psych.FatigueSensitivity);
        var stressIntensity = CalculateIntensity(state.Stress, psych.StressSensitivity);
        var socialNeedIntensity = CalculateIntensity(state.SocialNeed, psych.SocialSensitivity);
        var comfortIntensity = CalculateIntensity(state.Comfort, psych.ComfortSensitivity);
        var moodIntensity = CalculateIntensity(state.MoodScalar, psych.MoodReactivity);

        var hungerPerception = new HungerPerception(hungerLevel, hungerIntensity, state.Hunger);
        var energyPerception = new EnergyPerception(energyLevel, energyIntensity, state.Energy);
        var stressPerception = new StressPerception(stressLevel, stressIntensity, state.Stress);
        var socialNeedPerception = new SocialNeedPerception(socialNeedLevel, socialNeedIntensity, state.SocialNeed);
        var comfortPerception = new ComfortPerception(comfortLevel, comfortIntensity, state.Comfort);
        var moodPerception = new MoodPerception(moodLevel, moodIntensity, state.MoodScalar, state.Mood);

        // 4. Calculate Dominant Need with Deterministic Tie-Breaking Precedence:
        // Precedence: Hunger > Energy > SocialNeed > Comfort > Stress
        var dominantNeed = DetermineDominantNeed(state, psych);

        return new CharacterInternalExperience(
            CharacterId: characterId,
            StateVersion: state.Version,
            EvaluatedAtUtc: evaluatedAtUtc,
            Hunger: hungerPerception,
            Energy: energyPerception,
            Mood: moodPerception,
            Stress: stressPerception,
            SocialNeed: socialNeedPerception,
            Comfort: comfortPerception,
            DominantNeed: dominantNeed
        );
    }

    private static void ValidateMetric(decimal value, string paramName)
    {
        if (value < 0.00m || value > 100.00m)
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"State metric must be bounded in [0.00, 100.00]. Actual: {value}");
        }
    }

    private static void ValidateSensitivity(decimal value, string paramName)
    {
        if (value < 0.00m)
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"Psychology sensitivity trait cannot be negative. Actual: {value}");
        }
    }

    public static HungerLevel DiscretizeHunger(decimal hunger) => hunger switch
    {
        <= 20.00m => HungerLevel.Satisfied,
        <= 40.00m => HungerLevel.SlightlyHungry,
        <= 60.00m => HungerLevel.Hungry,
        <= 80.00m => HungerLevel.VeryHungry,
        _ => HungerLevel.Starving
    };

    public static EnergyLevel DiscretizeEnergy(decimal energy) => energy switch
    {
        <= 20.00m => EnergyLevel.Exhausted,
        <= 40.00m => EnergyLevel.Tired,
        <= 60.00m => EnergyLevel.Moderate,
        <= 80.00m => EnergyLevel.Energized,
        _ => EnergyLevel.HighlyEnergized
    };

    public static StressLevel DiscretizeStress(decimal stress) => stress switch
    {
        <= 20.00m => StressLevel.Calm,
        <= 40.00m => StressLevel.MildPressure,
        <= 60.00m => StressLevel.Stressed,
        <= 80.00m => StressLevel.HighlyStressed,
        _ => StressLevel.Overwhelmed
    };

    public static SocialNeedLevel DiscretizeSocialNeed(decimal socialNeed) => socialNeed switch
    {
        <= 20.00m => SocialNeedLevel.SociallySatisfied,
        <= 40.00m => SocialNeedLevel.MildSocialNeed,
        <= 60.00m => SocialNeedLevel.WantsCompany,
        <= 80.00m => SocialNeedLevel.StrongNeedForCompany,
        _ => SocialNeedLevel.CravesConnection
    };

    public static ComfortLevel DiscretizeComfort(decimal comfort) => comfort switch
    {
        <= 20.00m => ComfortLevel.VeryUncomfortable,
        <= 40.00m => ComfortLevel.Uncomfortable,
        <= 60.00m => ComfortLevel.Neutral,
        <= 80.00m => ComfortLevel.Comfortable,
        _ => ComfortLevel.VeryComfortable
    };

    public static MoodPerceptionLevel DiscretizeMood(decimal mood) => mood switch
    {
        <= 20.00m => MoodPerceptionLevel.Depressed,
        <= 40.00m => MoodPerceptionLevel.Low,
        <= 60.00m => MoodPerceptionLevel.Neutral,
        <= 80.00m => MoodPerceptionLevel.Good,
        _ => MoodPerceptionLevel.Elated
    };

    private static PerceptionIntensity CalculateIntensity(decimal rawMetric, decimal sensitivity)
    {
        decimal normalized = rawMetric / 100.00m;
        decimal scaled = normalized * sensitivity;
        double clamped = Math.Clamp((double)scaled, 0.0, 1.0);
        return new PerceptionIntensity(clamped);
    }

    private static DominantNeed DetermineDominantNeed(CharacterStateSnapshot state, PsychologyProfile psych)
    {
        decimal hungerPressure = (state.Hunger / 100.00m) * psych.HungerSensitivity;
        decimal fatiguePressure = ((100.00m - state.Energy) / 100.00m) * psych.FatigueSensitivity;
        decimal socialPressure = (state.SocialNeed / 100.00m) * psych.SocialSensitivity;
        decimal discomfortPressure = ((100.00m - state.Comfort) / 100.00m) * psych.ComfortSensitivity;
        decimal stressPressure = (state.Stress / 100.00m) * psych.StressSensitivity;

        const decimal BaselineThreshold = 0.20m;

        decimal maxPressure = Math.Max(
            hungerPressure,
            Math.Max(
                fatiguePressure,
                Math.Max(socialPressure, Math.Max(discomfortPressure, stressPressure))
            )
        );

        if (maxPressure <= BaselineThreshold)
        {
            return DominantNeed.None;
        }

        // Strict deterministic precedence: Hunger > Energy > SocialNeed > Comfort > Stress
        if (hungerPressure == maxPressure) return DominantNeed.Hunger;
        if (fatiguePressure == maxPressure) return DominantNeed.Energy;
        if (socialPressure == maxPressure) return DominantNeed.SocialNeed;
        if (discomfortPressure == maxPressure) return DominantNeed.Comfort;
        if (stressPressure == maxPressure) return DominantNeed.Stress;

        return DominantNeed.None;
    }

    #endregion
}
