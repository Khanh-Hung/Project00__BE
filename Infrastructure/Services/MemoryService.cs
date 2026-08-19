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

        var memoryRepo = _unitOfWork.GetRepository<CharacterMemory>();
        var allMemories = await memoryRepo.GetAllAsync(
            m => m.UserId == userId && m.CharacterId == characterId && !m.IsSoftDeleted,
            ct: ct);

        if (allMemories.Count == 0)
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

        var scored = allMemories
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
            memoryRepo.Update(mem);
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

        var memoryRepo = _unitOfWork.GetRepository<CharacterMemory>();
        var existingMemories = await memoryRepo.GetAllAsync(
            m => m.UserId == userId && m.CharacterId == characterId && !m.IsSoftDeleted,
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

            var key = $"{c.Type}_{normalized}";
            if (existingMap.TryGetValue(key, out var existing))
            {
                // Update importance & confidence if candidate has higher signals
                var newImportance = Math.Max(existing.Importance, c.Importance);
                var newConfidence = Math.Max(existing.Confidence, c.Confidence);
                existing.UpdateDetails(importance: newImportance, confidence: newConfidence);
                memoryRepo.Update(existing);
            }
            else
            {
                var newMemory = CharacterMemory.Create(
                    characterId: characterId,
                    userId: userId,
                    content: c.Content.Trim(),
                    type: c.Type,
                    importance: c.Importance,
                    confidence: c.Confidence,
                    sourceSessionId: sessionId
                );

                await memoryRepo.AddAsync(newMemory, ct);
                existingMap[key] = newMemory;
                addedCount++;
            }
        }

        await _unitOfWork.SaveChangesAsync(ct);
        return addedCount;
    }
}
