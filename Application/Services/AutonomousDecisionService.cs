using System.Security.Cryptography;
using System.Text;
using Application.Contracts.Activities;
using Application.Contracts.Autonomous;
using Application.Contracts.Goals;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Application.Services;

public sealed class AutonomousDecisionService : IAutonomousDecisionService
{
    private readonly ILogger<AutonomousDecisionService> _logger;

    public AutonomousDecisionService(ILogger<AutonomousDecisionService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<AutonomousDecisionResult> DecideNextActionAsync(
        AutonomousDecisionRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request, nameof(request));

        var charId = request.CharacterId;
        var time = request.CurrentTime;
        var state = request.StateSnapshot ?? throw new ArgumentException("Authoritative StateSnapshot is required for autonomous decisions.", nameof(request));
        var recentActs = request.RecentActivities ?? Array.Empty<CharacterActivity>();
        var recentActTypes = recentActs.Select(a => a.ActivityType).ToList();

        // 1. Generate base candidate pool based on time of day, personality, and world
        var pool = GenerateCandidatePool(request);

        // 2. Adjust candidate utility scores based on Character Physical & Psychological Needs
        ApplyStateAndNeedModifiers(pool, state, request.CurrentLocation);

        // 3. Filter Incompatible Activities based on physical state
        var compatiblePool = FilterIncompatibleActivities(pool, state);

        // 4. Apply Goal Relevance & Priority Boost
        ApplyGoalRelevanceBoost(compatiblePool, request.Goals, state);

        // 5. Filter by Cooldown and Anti-Repetition Policies
        var eligibleCandidates = new List<CandidateOption>();
        foreach (var opt in compatiblePool)
        {
            var lastActivityOfType = recentActs.FirstOrDefault(a => a.ActivityType == opt.Type);
            DateTime? lastPerformed = lastActivityOfType?.CompletedAt ?? lastActivityOfType?.StartedAt;

            bool onCooldown = ActivityCooldownPolicy.IsOnCooldown(opt.Type, lastPerformed, time);
            bool isRepetitive = ActivityCooldownPolicy.IsRepetitive(opt.Type, recentActTypes);

            if (!onCooldown && !isRepetitive)
            {
                eligibleCandidates.Add(opt);
            }
        }

        // Fallback to Resting / Idle if all filtered
        if (eligibleCandidates.Count == 0)
        {
            var fallbackType = state.Energy < 30 ? CharacterActivityType.Relaxing : CharacterActivityType.Idle;
            eligibleCandidates.Add(new CandidateOption(
                type: fallbackType,
                location: request.CurrentLocation,
                score: 10,
                actionHint: "resting quietly in place",
                poseHint: "seated relaxed",
                reason: "Cooldown and state constraints active; character taking a peaceful break."
            ));
        }

        // 6. Deterministic Selection & Tie-Breaking (No Randomness)
        // Order by: Score DESC -> GoalPriority DESC -> ActivityType (stable enum order) -> Location
        var selected = eligibleCandidates
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.GoalPriority)
            .ThenBy(c => (int)c.Type)
            .ThenBy(c => c.Location, StringComparer.Ordinal)
            .First();

        // 7. Evaluate Visual Moment Policy
        var lastVisualMemory = request.RecentVisualMemories?.OrderByDescending(m => m.CreatedAt).FirstOrDefault();
        DateTime? lastVisualAt = lastVisualMemory?.CreatedAt;

        var visualDecision = VisualMomentPolicy.Evaluate(
            activityType: selected.Type,
            activityLocation: selected.Location,
            currentVisualState: request.CurrentVisualState,
            currentTime: time,
            lastVisualGenerationAt: lastVisualAt
        );

        // 8. Compute Decision Fingerprint
        var fpBuilder = new StringBuilder();
        fpBuilder.Append(charId.ToString("N")).Append('|')
                 .Append(request.TimeBucket).Append('|')
                 .Append(selected.Type).Append('|')
                 .Append(selected.Location.ToLowerInvariant()).Append('|')
                 .Append(visualDecision.ShouldGenerate).Append('|')
                 .Append(request.SceneRevision);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fpBuilder.ToString()))).ToLowerInvariant();

        var finalReason = selected.GoalId.HasValue && !string.IsNullOrWhiteSpace(selected.GoalReason)
            ? $"{selected.Reason} | [Goal: {selected.GoalTitle}] {selected.GoalReason}"
            : selected.Reason;

        var candidate = new CharacterActivityCandidate(
            ActivityType: selected.Type,
            Location: selected.Location,
            Reason: finalReason,
            Priority: visualDecision.Priority,
            DurationMinutes: selected.DurationMinutes,
            ShouldCreateVisualMoment: visualDecision.ShouldGenerate,
            Confidence: 0.95f,
            ActionHint: selected.ActionHint,
            PoseHint: selected.PoseHint,
            OutfitHint: null,
            EnvironmentHint: null,
            DecisionFingerprint: fingerprint,
            GoalId: selected.GoalId,
            GoalTitle: selected.GoalTitle,
            GoalRelevance: selected.GoalRelevance,
            GoalReason: selected.GoalReason
        );

        var expectedDelta = CharacterActivityOutcomePolicy.CalculateDelta(selected.Type);

        _logger.LogInformation(
            "[AutonomousDecisionService] Decided action for CharacterId={CharacterId}, Type={Type}, Score={Score}, GoalId={GoalId}, VisualMoment={VisualMoment}, Fingerprint={Fingerprint}",
            charId, candidate.ActivityType, selected.Score, candidate.GoalId, candidate.ShouldCreateVisualMoment, fingerprint);

        var result = new AutonomousDecisionResult(
            Action: AutonomousDecisionAction.PerformActivity,
            Candidate: candidate,
            ExpectedStateDelta: expectedDelta,
            TargetGoalId: candidate.GoalId,
            Reason: finalReason
        );

        return Task.FromResult(result);
    }

    private sealed class CandidateOption
    {
        public CharacterActivityType Type { get; set; }
        public string Location { get; set; }
        public int Score { get; set; }
        public string ActionHint { get; set; }
        public string PoseHint { get; set; }
        public string Reason { get; set; }
        public int DurationMinutes { get; set; }
        public Guid? GoalId { get; set; }
        public string? GoalTitle { get; set; }
        public float? GoalRelevance { get; set; }
        public string? GoalReason { get; set; }
        public int GoalPriority { get; set; }

        public CandidateOption(
            CharacterActivityType type,
            string location,
            int score,
            string actionHint,
            string poseHint,
            string reason,
            int durationMinutes = 30,
            Guid? goalId = null,
            string? goalTitle = null,
            float? goalRelevance = null,
            string? goalReason = null,
            int goalPriority = 0)
        {
            Type = type;
            Location = location;
            Score = score;
            ActionHint = actionHint;
            PoseHint = poseHint;
            Reason = reason;
            DurationMinutes = durationMinutes;
            GoalId = goalId;
            GoalTitle = goalTitle;
            GoalRelevance = goalRelevance;
            GoalReason = goalReason;
            GoalPriority = goalPriority;
        }
    }

    private static void ApplyStateAndNeedModifiers(List<CandidateOption> pool, CharacterStateSnapshot state, string location)
    {
        // 1. Energy Tiers:
        // Tier A: Critical Exhaustion (Energy <= 20) -> Massive boost to Rest & Sleep (+300)
        // Tier B: Mild Tiredness (20 < Energy <= 30) -> Moderate boost to Rest (+80)
        if (state.Energy < 20)
        {
            var hasRest = pool.Any(opt => opt.Type == CharacterActivityType.Sleeping || opt.Type == CharacterActivityType.Relaxing);
            if (!hasRest)
            {
                pool.Add(new CandidateOption(
                    type: CharacterActivityType.Relaxing,
                    location: location,
                    score: 300,
                    actionHint: "resting quietly to recover energy",
                    poseHint: "seated or reclining comfortably",
                    reason: "Critical low energy detected; character taking a needed rest."
                ));
            }

            foreach (var opt in pool)
            {
                if (opt.Type == CharacterActivityType.Sleeping || opt.Type == CharacterActivityType.Relaxing)
                    opt.Score += 300;
                else if (opt.Type == CharacterActivityType.Exercising || opt.Type == CharacterActivityType.Exploring || opt.Type == CharacterActivityType.Working)
                    opt.Score = Math.Max(1, opt.Score / 5);
            }
        }
        else if (state.Energy <= 30)
        {
            var hasRest = pool.Any(opt => opt.Type == CharacterActivityType.Sleeping || opt.Type == CharacterActivityType.Relaxing);
            if (!hasRest)
            {
                pool.Add(new CandidateOption(
                    type: CharacterActivityType.Relaxing,
                    location: location,
                    score: 80,
                    actionHint: "resting quietly to regain stamina",
                    poseHint: "seated leaning back resting eyes",
                    reason: "Tiredness detected; pausing to recover."
                ));
            }

            foreach (var opt in pool)
            {
                if (opt.Type == CharacterActivityType.Sleeping || opt.Type == CharacterActivityType.Relaxing)
                    opt.Score += 80;
            }
        }

        // 2. Hunger Tiers:
        // Tier A: Critical Hunger (Hunger > 80) -> Massive boost to Eating (+300)
        // Tier B: High Hunger (60 <= Hunger <= 80) -> Moderate boost to Eating (+80)
        if (state.Hunger > 80)
        {
            var hasFood = pool.Any(opt => opt.Type == CharacterActivityType.Eating || opt.Type == CharacterActivityType.Cooking);
            if (!hasFood)
            {
                pool.Add(new CandidateOption(
                    type: CharacterActivityType.Eating,
                    location: location,
                    score: 300,
                    actionHint: "eating a satisfying meal",
                    poseHint: "sitting at table enjoying meal",
                    reason: "Critical hunger detected; immediate nourishment required."
                ));
            }

            foreach (var opt in pool)
            {
                if (opt.Type == CharacterActivityType.Eating || opt.Type == CharacterActivityType.Cooking)
                    opt.Score += 300;
            }
        }
        else if (state.Hunger >= 60)
        {
            var hasFood = pool.Any(opt => opt.Type == CharacterActivityType.Eating || opt.Type == CharacterActivityType.Cooking);
            if (!hasFood)
            {
                pool.Add(new CandidateOption(
                    type: CharacterActivityType.Eating,
                    location: location,
                    score: 80,
                    actionHint: "having a nourishing meal",
                    poseHint: "seated at table enjoying food",
                    reason: "High hunger detected; character stopping for a meal."
                ));
            }

            foreach (var opt in pool)
            {
                if (opt.Type == CharacterActivityType.Eating || opt.Type == CharacterActivityType.Cooking)
                    opt.Score += 80;
            }
        }

        // 3. High Social Need -> Heavily prioritize Socializing
        if (state.SocialNeed > 60)
        {
            var hasSocial = pool.Any(opt => opt.Type == CharacterActivityType.Socializing);
            if (!hasSocial)
            {
                pool.Add(new CandidateOption(
                    type: CharacterActivityType.Socializing,
                    location: location,
                    score: 250,
                    actionHint: "conversing warmly with friends and companions",
                    poseHint: "standing or seated engaged in conversation",
                    reason: "High social need detected; character seeking company."
                ));
            }

            foreach (var opt in pool)
            {
                if (opt.Type == CharacterActivityType.Socializing)
                    opt.Score += 250;
            }
        }

        // 4. High Stress -> Prioritize Relaxation & Bathing
        if (state.Stress > 70)
        {
            var hasRelief = pool.Any(opt => opt.Type == CharacterActivityType.Relaxing || opt.Type == CharacterActivityType.Bathing);
            if (!hasRelief)
            {
                pool.Add(new CandidateOption(
                    type: CharacterActivityType.Relaxing,
                    location: location,
                    score: 200,
                    actionHint: "taking a quiet moment to destress",
                    poseHint: "seated peacefully",
                    reason: "High stress detected; character taking time to unwind."
                ));
            }

            foreach (var opt in pool)
            {
                if (opt.Type == CharacterActivityType.Relaxing || opt.Type == CharacterActivityType.Bathing)
                    opt.Score += 200;
            }
        }

        // 5. Low Comfort -> Prioritize Relaxation & Rest
        if (state.Comfort <= 20)
        {
            var hasComfort = pool.Any(opt => opt.Type == CharacterActivityType.Relaxing || opt.Type == CharacterActivityType.Sleeping);
            if (!hasComfort)
            {
                pool.Add(new CandidateOption(
                    type: CharacterActivityType.Relaxing,
                    location: location,
                    score: 150,
                    actionHint: "seeking a warm, comfortable space to rest",
                    poseHint: "reclining comfortably",
                    reason: "Low comfort detected; character seeking a restorative space."
                ));
            }

            foreach (var opt in pool)
            {
                if (opt.Type == CharacterActivityType.Relaxing || opt.Type == CharacterActivityType.Sleeping)
                    opt.Score += 150;
            }
        }
    }

    private static List<CandidateOption> FilterIncompatibleActivities(List<CandidateOption> pool, CharacterStateSnapshot state)
    {
        var result = new List<CandidateOption>();
        foreach (var opt in pool)
        {
            // Incompatibility 1: Exhausted (Energy < 20) cannot do intense physical exercise, expeditions, or heavy working
            if (state.Energy < 20 && (opt.Type == CharacterActivityType.Exercising || opt.Type == CharacterActivityType.Exploring || opt.Type == CharacterActivityType.Working))
            {
                continue;
            }

            // Incompatibility 2: Extremely stressed (> 85) cannot do heavy overtime working
            if (state.Stress > 85 && opt.Type == CharacterActivityType.Working)
            {
                continue;
            }

            result.Add(opt);
        }

        return result.Count > 0 ? result : pool;
    }

    private static void ApplyGoalRelevanceBoost(
        List<CandidateOption> pool,
        IReadOnlyList<CharacterGoalSnapshot>? goals,
        CharacterStateSnapshot state)
    {
        if (goals == null || goals.Count == 0)
            return;

        var activeGoals = goals.Where(g => g.Status == CharacterGoalStatus.Active).ToList();
        if (activeGoals.Count == 0)
            return;

        // If physical needs are in critical range (Energy < 20 or Hunger > 80), cap goal boost so survival needs win
        bool criticalPhysicalNeed = state.Energy < 20 || state.Hunger > 80;

        foreach (var opt in pool)
        {
            var bestGoalMatch = activeGoals
                .Select(g => new
                {
                    Goal = g,
                    Relevance = GoalActivityRelevancePolicy.Evaluate(g.Title, g.Description, g.GoalType, opt.Type),
                    PriorityWeight = (int)g.Priority
                })
                .Where(x => x.Relevance.Score > 0.05f)
                .OrderByDescending(x => x.PriorityWeight)
                .ThenByDescending(x => x.Relevance.Score)
                .ThenBy(x => x.Goal.GoalId)
                .FirstOrDefault();

            if (bestGoalMatch != null)
            {
                int priorityMultiplier = bestGoalMatch.Goal.Priority switch
                {
                    CharacterGoalPriority.Critical => 15,
                    CharacterGoalPriority.High => 8,
                    CharacterGoalPriority.Normal => 4,
                    _ => 1
                };

                int maxBoost = criticalPhysicalNeed ? 20 : 150;
                int goalBoost = Math.Min(maxBoost, (int)(bestGoalMatch.Relevance.Score * priorityMultiplier * 15));

                opt.Score += goalBoost;
                opt.GoalId = bestGoalMatch.Goal.GoalId;
                opt.GoalTitle = bestGoalMatch.Goal.Title;
                opt.GoalRelevance = bestGoalMatch.Relevance.Score;
                opt.GoalReason = bestGoalMatch.Relevance.Reason;
                opt.GoalPriority = (int)bestGoalMatch.Goal.Priority;
            }
        }
    }

    private static List<CandidateOption> GenerateCandidatePool(AutonomousDecisionRequest req)
    {
        var pool = new List<CandidateOption>();
        int hour = req.CurrentTime.Hour;
        var pPrompt = (req.PersonalityPrompt ?? "").ToLowerInvariant();
        var goalText = (req.Goals != null ? string.Join(" ", req.Goals.Select(g => g.Title + " " + g.GoalType + " " + (g.Description ?? ""))) : "") + " " +
                       (req.ActiveGoals != null ? string.Join(" ", req.ActiveGoals) : "");
        var goals = goalText.ToLowerInvariant();
        var loc = req.CurrentLocation;

        bool isScholar = pPrompt.Contains("scholar") || pPrompt.Contains("academic") || pPrompt.Contains("research") || pPrompt.Contains("arcane");
        bool isAdventurer = pPrompt.Contains("warrior") || pPrompt.Contains("knight") || pPrompt.Contains("adventurer") || pPrompt.Contains("explore");
        bool isGettingReady = pPrompt.Contains("getting ready") || pPrompt.Contains("grooming") || pPrompt.Contains("morning routine");
        bool isGoalExplore = goals.Contains("explore") || goals.Contains("travel") || goals.Contains("discover");
        bool isGoalStudy = goals.Contains("study") || goals.Contains("research") || goals.Contains("investigate");

        // Time-of-day baseline candidate generation
        if (hour >= 23 || hour < 6)
        {
            pool.Add(new CandidateOption(CharacterActivityType.Sleeping, loc, 80, "sleeping peacefully", "lying down relaxed", "Nighttime rest period", 360));
            pool.Add(new CandidateOption(CharacterActivityType.Relaxing, loc, 30, "resting quietly by candlelight", "seated comfortably", "Late night quiet relaxation", 60));
            if (isScholar)
            {
                pool.Add(new CandidateOption(CharacterActivityType.Reading, loc, 40, "studying ancient manuscripts late into the night", "seated at study desk", "Late night scholarly study", 45));
            }
        }
        else if (hour >= 6 && hour < 9)
        {
            if (isGettingReady)
                pool.Add(new CandidateOption(CharacterActivityType.GettingReady, loc, 90, "preparing attire and grooming for the day", "standing in front of a mirror", "Morning routine preparation", 30));
            else
            {
                pool.Add(new CandidateOption(CharacterActivityType.GettingReady, loc, 40, "preparing attire and grooming for the day", "standing in front of a mirror", "Morning routine preparation", 30));
                pool.Add(new CandidateOption(CharacterActivityType.Eating, loc, 35, "having a light breakfast", "seated at breakfast table", "Morning meal", 30));
                if (isAdventurer)
                    pool.Add(new CandidateOption(CharacterActivityType.Exercising, loc, 30, "morning physical conditioning and sword practice", "mid-motion dynamic athletic pose", "Morning physical drill", 45));
            }
        }
        else if (hour >= 9 && hour < 12)
        {
            if (isGoalExplore)
                pool.Add(new CandidateOption(CharacterActivityType.Exploring, loc, 90, "scouting and surveying the surroundings", "standing attentively observing the area", "Morning exploration goal", 60));
            else if (isAdventurer)
            {
                pool.Add(new CandidateOption(CharacterActivityType.Exploring, loc, 70, "scouting and surveying the surroundings", "standing attentively observing the area", "Morning exploration", 60));
                pool.Add(new CandidateOption(CharacterActivityType.Walking, loc, 25, "surveying territory", "walking actively", "Territory patrol", 45));
            }
            else if (isScholar || isGoalStudy)
            {
                pool.Add(new CandidateOption(CharacterActivityType.Working, loc, 50, "analyzing arcane research notes and cataloging scrolls", "seated at desk surrounded by tomes", "Scholarly research session", 90));
                pool.Add(new CandidateOption(CharacterActivityType.Reading, loc, 40, "browsing through reference texts", "seated comfortably reading", "General reading", 45));
            }
            else
            {
                pool.Add(new CandidateOption(CharacterActivityType.Working, loc, 35, "focusing diligently on daily tasks", "standing or seated actively engaged", "Focus work", 60));
                pool.Add(new CandidateOption(CharacterActivityType.Reading, loc, 25, "browsing through reference texts", "seated comfortably reading", "General reading", 45));
            }
        }
        else if (hour >= 12 && hour < 14)
        {
            pool.Add(new CandidateOption(CharacterActivityType.Eating, loc, 45, "eating a midday meal", "seated at dining table", "Lunch meal", 30));
            pool.Add(new CandidateOption(CharacterActivityType.Walking, loc, 30, "taking a leisurely stroll in the courtyard", "walking gracefully along path", "Midday walk", 30));
            pool.Add(new CandidateOption(CharacterActivityType.Socializing, loc, 25, "conversing pleasantly with companions", "standing or seated engaged in conversation", "Social interaction", 45));
        }
        else if (hour >= 14 && hour < 18)
        {
            if (isAdventurer || isGoalExplore)
                pool.Add(new CandidateOption(CharacterActivityType.Exploring, loc, 70, "exploring new wings and uncharted territory", "navigating through scenic arches", "Afternoon expedition", 90));

            if (isScholar || isGoalStudy)
                pool.Add(new CandidateOption(CharacterActivityType.Reading, loc, 50, "deeply engrossed in reading scholarly volumes", "seated beside window reading", "Afternoon study", 60));

            pool.Add(new CandidateOption(CharacterActivityType.Working, loc, 30, "tending to ongoing responsibilities", "actively engaged in task", "Afternoon work", 60));
            pool.Add(new CandidateOption(CharacterActivityType.Exercising, loc, 25, "practicing tactical forms and stretching", "dynamic balanced athletic pose", "Physical practice", 45));
        }
        else if (hour >= 18 && hour < 21)
        {
            pool.Add(new CandidateOption(CharacterActivityType.Cooking, loc, 40, "preparing a warm evening meal", "standing near hearth or cooking counter", "Evening meal preparation", 45));
            pool.Add(new CandidateOption(CharacterActivityType.Eating, loc, 35, "enjoying dinner", "seated at dining table", "Evening dinner", 45));
            pool.Add(new CandidateOption(CharacterActivityType.Socializing, loc, 30, "relaxing and chatting about the day's events", "seated warmly by the fire", "Evening fellowship", 60));
        }
        else
        {
            pool.Add(new CandidateOption(CharacterActivityType.Bathing, loc, 40, "taking a soothing warm bath", "immersed in steaming bath water", "Evening bath and unwind", 45));
            pool.Add(new CandidateOption(CharacterActivityType.Relaxing, loc, 35, "unwinding by the fireplace with a warm drink", "seated comfortably in armchair", "Evening wind-down", 45));
            pool.Add(new CandidateOption(CharacterActivityType.Reading, loc, 30, "reading a novel before bed", "reclining comfortably reading", "Bedtime reading", 45));
        }

        return pool;
    }
}
