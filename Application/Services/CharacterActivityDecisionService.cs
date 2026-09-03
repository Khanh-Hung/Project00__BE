using System.Security.Cryptography;
using System.Text;
using Application.Contracts.Activities;
using Application.Contracts.Goals;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Microsoft.Extensions.Logging;

namespace Application.Services;

public sealed class CharacterActivityDecisionService : ICharacterActivityDecisionService
{
    private readonly ILogger<CharacterActivityDecisionService> _logger;

    public CharacterActivityDecisionService(ILogger<CharacterActivityDecisionService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<CharacterActivityCandidate?> DecideAsync(
        CharacterActivityDecisionRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request, nameof(request));

        var charId = request.CharacterId;
        var time = request.CurrentTime;
        var recentActs = request.RecentActivities ?? Array.Empty<CharacterActivity>();
        var recentActTypes = recentActs.Select(a => a.ActivityType).ToList();

        // 1. Determine eligible candidate pool based on Time of Day, Location & Context
        var pool = GenerateCandidatePool(request);

        // 1.1 Apply Goal Relevance Scoring & Ranking if active goals exist
        if (request.Goals != null && request.Goals.Count > 0)
        {
            var activeGoals = request.Goals.Where(g => g.Status == CharacterGoalStatus.Active).ToList();
            if (activeGoals.Count > 0)
            {
                foreach (var opt in pool)
                {
                    // Find highest matching goal for this activity candidate
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
                            CharacterGoalPriority.Critical => 20,
                            CharacterGoalPriority.High => 8,
                            CharacterGoalPriority.Normal => 4,
                            _ => 1
                        };
                        int goalBoost = (int)(bestGoalMatch.Relevance.Score * priorityMultiplier * 100);
                        opt.Weight += goalBoost;
                        opt.GoalId = bestGoalMatch.Goal.GoalId;
                        opt.GoalTitle = bestGoalMatch.Goal.Title;
                        opt.GoalRelevance = bestGoalMatch.Relevance.Score;
                        opt.GoalReason = bestGoalMatch.Relevance.Reason;
                    }
                }
            }
        }

        // 2. Filter pool by Cooldown and Anti-Repetition Policies
        var eligibleCandidates = new List<CandidateOption>();
        foreach (var opt in pool)
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

        // Fallback to Idle / Relaxing if all candidates filtered
        if (eligibleCandidates.Count == 0)
        {
            eligibleCandidates.Add(new CandidateOption(
                type: CharacterActivityType.Idle,
                location: request.CurrentLocation,
                weight: 10,
                actionHint: "resting quietly in place",
                poseHint: "standing or seated relaxed",
                reason: "Cooldown constraints active; character taking a quiet break."
            ));
        }

        // 3. Deterministic Seed Generation
        var seedString = $"{charId:N}:{request.TimeBucket}:{request.SceneRevision}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(seedString));
        var seed = BitConverter.ToUInt32(hashBytes, 0);

        // 4. Weighted Deterministic Selection
        int totalWeight = eligibleCandidates.Sum(c => c.Weight);
        uint roll = seed % (uint)Math.Max(1, totalWeight);

        CandidateOption selected = eligibleCandidates[0];
        int runningWeight = 0;
        foreach (var cand in eligibleCandidates)
        {
            runningWeight += cand.Weight;
            if (roll < runningWeight)
            {
                selected = cand;
                break;
            }
        }

        // 5. Evaluate Visual Moment Policy
        var lastVisualMemory = request.RecentVisualMemories?.OrderByDescending(m => m.CreatedAt).FirstOrDefault();
        DateTime? lastVisualAt = lastVisualMemory?.CreatedAt;

        var visualDecision = VisualMomentPolicy.Evaluate(
            activityType: selected.Type,
            activityLocation: selected.Location,
            currentVisualState: request.CurrentVisualState,
            currentTime: time,
            lastVisualGenerationAt: lastVisualAt
        );

        // 6. Compute Decision Fingerprint
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

        _logger.LogInformation(
            "[CharacterActivityDecisionService] Evaluated activity for CharacterId={CharacterId}, Type={Type}, Location='{Location}', VisualMoment={ShouldGenerate}, GoalId={GoalId}, Fingerprint={Fingerprint}",
            charId, candidate.ActivityType, candidate.Location, candidate.ShouldCreateVisualMoment, candidate.GoalId, fingerprint);

        return Task.FromResult<CharacterActivityCandidate?>(candidate);
    }

    private sealed class CandidateOption
    {
        public CharacterActivityType Type { get; set; }
        public string Location { get; set; }
        public int Weight { get; set; }
        public string ActionHint { get; set; }
        public string PoseHint { get; set; }
        public string Reason { get; set; }
        public int DurationMinutes { get; set; }
        public Guid? GoalId { get; set; }
        public string? GoalTitle { get; set; }
        public float? GoalRelevance { get; set; }
        public string? GoalReason { get; set; }

        public CandidateOption(
            CharacterActivityType type,
            string location,
            int weight,
            string actionHint,
            string poseHint,
            string reason,
            int durationMinutes = 30,
            Guid? goalId = null,
            string? goalTitle = null,
            float? goalRelevance = null,
            string? goalReason = null)
        {
            Type = type;
            Location = location;
            Weight = weight;
            ActionHint = actionHint;
            PoseHint = poseHint;
            Reason = reason;
            DurationMinutes = durationMinutes;
            GoalId = goalId;
            GoalTitle = goalTitle;
            GoalRelevance = goalRelevance;
            GoalReason = goalReason;
        }
    }

    private static List<CandidateOption> GenerateCandidatePool(CharacterActivityDecisionRequest req)
    {
        var pool = new List<CandidateOption>();
        int hour = req.CurrentTime.Hour;
        var pPrompt = (req.PersonalityPrompt ?? "").ToLowerInvariant();
        var goalText = (req.Goals != null ? string.Join(" ", req.Goals.Select(g => g.Title + " " + g.GoalType + " " + (g.Description ?? ""))) : "") + " " +
                       (req.ActiveGoals != null ? string.Join(" ", req.ActiveGoals) : "");
        var goals = goalText.ToLowerInvariant();
        var world = (req.WorldDescription ?? "").ToLowerInvariant();
        var loc = req.CurrentLocation;

        bool isScholar = pPrompt.Contains("scholar") || pPrompt.Contains("academic") || pPrompt.Contains("research") || pPrompt.Contains("arcane");
        bool isAdventurer = pPrompt.Contains("warrior") || pPrompt.Contains("knight") || pPrompt.Contains("adventurer") || pPrompt.Contains("explore");
        bool isGettingReady = pPrompt.Contains("getting ready") || pPrompt.Contains("grooming") || pPrompt.Contains("morning routine");
        bool isGoalExplore = goals.Contains("explore") || goals.Contains("travel") || goals.Contains("discover");
        bool isGoalStudy = goals.Contains("study") || goals.Contains("research") || goals.Contains("investigate");

        // Time-based baseline
        if (hour >= 23 || hour < 6)
        {
            // Night
            pool.Add(new CandidateOption(CharacterActivityType.Sleeping, loc, 50, "sleeping peacefully", "lying down relaxed", "Nighttime rest period", 360));
            pool.Add(new CandidateOption(CharacterActivityType.Relaxing, loc, 20, "resting quietly by candlelight", "seated comfortably", "Late night quiet relaxation", 60));
            if (isScholar)
            {
                pool.Add(new CandidateOption(CharacterActivityType.Reading, loc, 35, "studying ancient manuscripts late into the night", "seated at study desk", "Late night scholarly study", 45));
            }
        }
        else if (hour >= 6 && hour < 9)
        {
            // Morning
            if (isGettingReady)
            {
                pool.Add(new CandidateOption(CharacterActivityType.GettingReady, loc, 100, "preparing attire and grooming for the day", "standing in front of a mirror", "Morning routine preparation", 30));
            }
            else
            {
                pool.Add(new CandidateOption(CharacterActivityType.GettingReady, loc, 40, "preparing attire and grooming for the day", "standing in front of a mirror", "Morning routine preparation", 30));
                pool.Add(new CandidateOption(CharacterActivityType.Eating, loc, 30, "having a light breakfast", "seated at breakfast table", "Morning meal", 30));
                if (isAdventurer)
                {
                    pool.Add(new CandidateOption(CharacterActivityType.Exercising, loc, 30, "morning physical conditioning and sword practice", "mid-motion dynamic athletic pose", "Morning physical drill", 45));
                }
            }
        }
        else if (hour >= 9 && hour < 12)
        {
            // Forenoon
            if (isGoalExplore)
            {
                pool.Add(new CandidateOption(CharacterActivityType.Exploring, loc, 100, "scouting and surveying the surroundings", "standing attentively observing the area", "Morning exploration goal", 60));
            }
            else if (isAdventurer)
            {
                pool.Add(new CandidateOption(CharacterActivityType.Exploring, loc, 80, "scouting and surveying the surroundings", "standing attentively observing the area", "Morning exploration", 60));
                pool.Add(new CandidateOption(CharacterActivityType.Walking, loc, 20, "surveying territory", "walking actively", "Territory patrol", 45));
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
            // Midday
            pool.Add(new CandidateOption(CharacterActivityType.Eating, loc, 40, "eating a midday meal", "seated at dining table", "Lunch meal", 30));
            pool.Add(new CandidateOption(CharacterActivityType.Walking, loc, 30, "taking a leisurely stroll in the courtyard", "walking gracefully along path", "Midday walk", 30));
            pool.Add(new CandidateOption(CharacterActivityType.Socializing, loc, 20, "conversing pleasantly with companions", "standing or seated engaged in conversation", "Social interaction", 45));
        }
        else if (hour >= 14 && hour < 18)
        {
            // Afternoon
            if (isAdventurer || isGoalExplore)
                pool.Add(new CandidateOption(CharacterActivityType.Exploring, loc, 80, "exploring new wings and uncharted territory", "navigating through scenic arches", "Afternoon expedition", 90));

            if (isScholar || isGoalStudy)
                pool.Add(new CandidateOption(CharacterActivityType.Reading, loc, 50, "deeply engrossed in reading scholarly volumes", "seated beside window reading", "Afternoon study", 60));

            pool.Add(new CandidateOption(CharacterActivityType.Working, loc, 30, "tending to ongoing responsibilities", "actively engaged in task", "Afternoon work", 60));
            pool.Add(new CandidateOption(CharacterActivityType.Exercising, loc, 25, "practicing tactical forms and stretching", "dynamic balanced athletic pose", "Physical practice", 45));
        }
        else if (hour >= 18 && hour < 21)
        {
            // Evening
            pool.Add(new CandidateOption(CharacterActivityType.Cooking, loc, 35, "preparing a warm evening meal", "standing near hearth or cooking counter", "Evening meal preparation", 45));
            pool.Add(new CandidateOption(CharacterActivityType.Eating, loc, 35, "enjoying dinner", "seated at dining table", "Evening dinner", 45));
            pool.Add(new CandidateOption(CharacterActivityType.Socializing, loc, 30, "relaxing and chatting about the day's events", "seated warmly by the fire", "Evening fellowship", 60));
        }
        else
        {
            // Late Evening (21:00 - 23:00)
            pool.Add(new CandidateOption(CharacterActivityType.Bathing, loc, 35, "taking a soothing warm bath", "immersed in steaming bath water", "Evening bath and unwind", 45));
            pool.Add(new CandidateOption(CharacterActivityType.Relaxing, loc, 35, "unwinding by the fireplace with a warm drink", "seated comfortably in armchair", "Evening wind-down", 45));
            pool.Add(new CandidateOption(CharacterActivityType.Reading, loc, 30, "reading a novel before bed", "reclining comfortably reading", "Bedtime reading", 45));
        }

        return pool;
    }
}
