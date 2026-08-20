using Application.Abstractions.Data;
using Application.Common;
using Application.Interfaces;
using Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Application.Services;

public sealed class LorebookEngine : ILorebookEngine
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<LorebookEngine> _logger;

    public LorebookEngine(IUnitOfWork unitOfWork, ILogger<LorebookEngine> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LorebookEntry>> MatchLorebookEntriesAsync(
        Guid characterId,
        string userMessage,
        IReadOnlyList<ChatMessage> recentMessages,
        int maxTokenBudget = 800,
        CancellationToken ct = default)
    {
        var repo = _unitOfWork.GetRepository<LorebookEntry>();
        
        // Fetch enabled lorebook entries for this character + universal lore (CharacterId == null)
        var allEntries = await repo.FindAsync(
            e => e.IsEnabled && (e.CharacterId == characterId || e.CharacterId == null),
            ct);

        if (!allEntries.Any()) return Array.Empty<LorebookEntry>();

        var constantEntries = allEntries.Where(e => e.IsConstant).OrderByDescending(e => e.Priority).ToList();
        var dynamicEntries = allEntries.Where(e => !e.IsConstant).OrderByDescending(e => e.Priority).ToList();

        // Build search haystack from user message + last 3 recent messages
        var searchHaystackBuilder = new System.Text.StringBuilder();
        searchHaystackBuilder.AppendLine(userMessage);
        foreach (var msg in recentMessages.TakeLast(3))
        {
            searchHaystackBuilder.AppendLine(msg.Content);
        }
        var searchHaystack = searchHaystackBuilder.ToString().ToLowerInvariant();

        var matchedDynamic = new List<LorebookEntry>();
        foreach (var entry in dynamicEntries)
        {
            if (entry.Keywords.Count == 0) continue;

            bool isMatched = entry.Keywords.Any(k =>
                !string.IsNullOrWhiteSpace(k) &&
                searchHaystack.Contains(k.Trim().ToLowerInvariant()));

            if (isMatched)
            {
                matchedDynamic.Add(entry);
            }
        }

        // Budgeting: Pack constant entries first, then highest priority matched dynamic entries
        var result = new List<LorebookEntry>();
        int currentTokens = 0;

        foreach (var entry in constantEntries)
        {
            var cost = TokenEstimator.EstimateTokenCount(entry.Title + " " + entry.Content);
            if (currentTokens + cost <= maxTokenBudget)
            {
                result.Add(entry);
                currentTokens += cost;
            }
        }

        foreach (var entry in matchedDynamic)
        {
            var cost = TokenEstimator.EstimateTokenCount(entry.Title + " " + entry.Content);
            if (currentTokens + cost <= maxTokenBudget)
            {
                result.Add(entry);
                currentTokens += cost;
            }
        }

        _logger.LogInformation("Matched {Count} lorebook entries (Constant: {Constant}, Dynamic: {Dynamic}, Tokens: ~{Tokens}) for character {CharacterId}",
            result.Count, result.Count(e => e.IsConstant), result.Count(e => !e.IsConstant), currentTokens, characterId);

        return result;
    }
}
