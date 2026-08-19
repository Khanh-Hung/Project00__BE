using System.Text.RegularExpressions;
using Application.Abstractions.Data;
using Application.DTOs;
using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class MemoryService : IMemoryService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<MemoryService> _logger;

    public MemoryService(IUnitOfWork unitOfWork, ILogger<MemoryService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public static string NormalizeContent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lower = text.Trim().ToLowerInvariant();
        return Regex.Replace(lower, @"\s+", " ");
    }

    /// <summary>
    /// Phase 2.1 2-Phase Retrieval (Top Important [limit 20] + Most Recent [limit 20] -> In-memory Score & Diversity -> Top 6).
    /// Semantic retrieval will be introduced when pgvector is integrated in Phase 2.2.
    /// </summary>
    public async Task<IReadOnlyList<CharacterMemory>> GetRelevantMemoriesAsync(
        Guid userId,
        Guid characterId,
        int maxCount = 6,
        CancellationToken ct = default)
    {
        if (userId == Guid.Empty || characterId == Guid.Empty || maxCount <= 0)
        {
            return Array.Empty<CharacterMemory>();
        }

        // 2-Phase Retrieval: Query top important and most recent separately to avoid full table scans in memory
        var importantTask = _unitOfWork.CharacterMemories.GetTopImportantAsync(userId, characterId, minImportance: 3, limit: 20, ct: ct);
        var recentTask = _unitOfWork.CharacterMemories.GetMostRecentAsync(userId, characterId, limit: 20, ct: ct);

        await Task.WhenAll(importantTask, recentTask);

        var topImportant = await importantTask;
        var mostRecent = await recentTask;

        // Combine and distinct candidates in-memory (at most 40 items)
        var combinedCandidates = topImportant
            .Concat(mostRecent)
            .GroupBy(m => m.Id)
            .Select(g => g.First())
            .ToList();

        if (combinedCandidates.Count == 0)
        {
            return Array.Empty<CharacterMemory>();
        }

        var now = Clock.Now;

        // Score = (Importance * 2.0) + RecencyBonus
        double CalculateScore(CharacterMemory m)
        {
            var ageDays = Math.Max(0.0, (now - m.CreatedAt).TotalDays);
            var recencyBonus = Math.Max(0.0, 5.0 - (ageDays * 0.25)); // +5 for today, decaying gradually
            return (m.Importance * 2.0) + (double)m.Confidence + recencyBonus;
        }

        var scored = combinedCandidates
            .Select(m => new { Memory = m, Score = CalculateScore(m) })
            .OrderByDescending(x => x.Score)
            .ToList();

        // Apply Diversity Selection: Pick top 1 from distinct types first, then fill remaining slots by score
        var selected = new List<CharacterMemory>();
        var seenIds = new HashSet<Guid>();

        var byType = scored.GroupBy(x => x.Memory.Type).ToList();
        foreach (var group in byType)
        {
            var bestInGroup = group.First();
            if (selected.Count < maxCount && seenIds.Add(bestInGroup.Memory.Id))
            {
                selected.Add(bestInGroup.Memory);
            }
        }

        // Fill remaining slots
        foreach (var item in scored)
        {
            if (selected.Count >= maxCount) break;
            if (seenIds.Add(item.Memory.Id))
            {
                selected.Add(item.Memory);
            }
        }

        // Touch LastAccessedAt
        foreach (var mem in selected)
        {
            mem.MarkAccessed(now);
            _unitOfWork.CharacterMemories.Update(mem);
        }

        try
        {
            await _unitOfWork.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed updating LastAccessedAt for retrieved memories");
        }

        return selected;
    }

    public async Task<int> StoreCandidatesAsync(
        Guid userId,
        Guid characterId,
        Guid? sessionId,
        IEnumerable<MemoryCandidate> candidates,
        CancellationToken ct = default)
    {
        if (userId == Guid.Empty || characterId == Guid.Empty)
        {
            return 0;
        }

        var candidateList = candidates?.ToList() ?? new List<MemoryCandidate>();
        if (candidateList.Count == 0)
        {
            return 0;
        }

        // Query only existing memories matching the candidate types instead of loading entire memory set
        var candidateTypes = candidateList.Select(c => c.Type).Distinct().ToList();
        var existingMemories = await _unitOfWork.CharacterMemories.GetExistingByTypesAsync(
            userId,
            characterId,
            candidateTypes,
            ct: ct);

        // Map existing normalized content + type to memory
        var existingMap = new Dictionary<string, CharacterMemory>();
        foreach (var m in existingMemories)
        {
            var key = $"{m.Type}_{NormalizeContent(m.Content)}";
            existingMap[key] = m;
        }

        int addedCount = 0;
        foreach (var c in candidateList)
        {
            if (string.IsNullOrWhiteSpace(c.Content)) continue;

            var normalized = NormalizeContent(c.Content);
            if (string.IsNullOrEmpty(normalized)) continue;

            // Application sanitization of untrusted AI candidate before persisting
            var sanitizedContent = c.Content.Trim();
            if (sanitizedContent.Length > 1000)
            {
                sanitizedContent = sanitizedContent.Substring(0, 1000).Trim();
            }
            var sanitizedImportance = Math.Clamp(c.Importance, 1, 5);
            var sanitizedConfidence = Math.Clamp(c.Confidence, 0.0m, 1.0m);

            var key = $"{c.Type}_{normalized}";
            if (existingMap.TryGetValue(key, out var existing))
            {
                // Update importance & confidence if candidate has higher signals
                var newImportance = Math.Max(existing.Importance, sanitizedImportance);
                var newConfidence = Math.Max(existing.Confidence, sanitizedConfidence);
                existing.UpdateDetails(importance: newImportance, confidence: newConfidence);
                _unitOfWork.CharacterMemories.Update(existing);
            }
            else
            {
                var newMemory = CharacterMemory.Create(
                    characterId: characterId,
                    userId: userId,
                    content: sanitizedContent,
                    type: c.Type,
                    importance: sanitizedImportance,
                    confidence: sanitizedConfidence,
                    sourceSessionId: sessionId
                );

                await _unitOfWork.CharacterMemories.AddAsync(newMemory, ct);
                existingMap[key] = newMemory;
                addedCount++;
            }
        }

        await _unitOfWork.SaveChangesAsync(ct);
        return addedCount;
    }
}
