using System.Text.RegularExpressions;
using Application.Abstractions.Data;
using Application.Common;
using Application.DTOs;
using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class MemoryService : IMemoryService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMemoryCandidateValidator _validator;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<MemoryService> _logger;

    public MemoryService(
        IUnitOfWork unitOfWork,
        IMemoryCandidateValidator validator,
        IEmbeddingService embeddingService,
        ILogger<MemoryService> logger)
    {
        _unitOfWork = unitOfWork;
        _validator = validator;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public static string NormalizeContent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lower = text.Trim().ToLowerInvariant();
        return Regex.Replace(lower, @"\s+", " ");
    }

    /// <summary>
    /// Semantic Hybrid Retrieval: Combines Vector Similarity + Importance + Recency + Category Diversity.
    /// </summary>
    public async Task<IReadOnlyList<CharacterMemory>> GetRelevantMemoriesAsync(
        Guid userId,
        Guid characterId,
        int maxCount = 6,
        string? queryText = null,
        CancellationToken ct = default)
    {
        if (userId == Guid.Empty || characterId == Guid.Empty || maxCount <= 0)
        {
            return Array.Empty<CharacterMemory>();
        }

        var topImportant = await _unitOfWork.CharacterMemories.GetTopImportantAsync(userId, characterId, minImportance: 3, limit: 20, ct: ct);
        var mostRecent = await _unitOfWork.CharacterMemories.GetMostRecentAsync(userId, characterId, limit: 20, ct: ct);

        var combinedCandidates = topImportant
            .Concat(mostRecent)
            .GroupBy(m => m.Id)
            .Select(g => g.First())
            .ToList();

        if (combinedCandidates.Count == 0)
        {
            return Array.Empty<CharacterMemory>();
        }

        float[]? queryEmbedding = null;
        if (!string.IsNullOrWhiteSpace(queryText))
        {
            try
            {
                queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(queryText, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate query embedding for memory retrieval");
            }
        }

        var now = Clock.Now;

        // Calculate Hybrid Semantic Score
        double CalculateScore(CharacterMemory m)
        {
            var ageDays = Math.Max(0.0, (now - m.CreatedAt).TotalDays);
            var recencyBonus = Math.Max(0.0, 5.0 - (ageDays * 0.25)); // +5 for today, decaying gradually
            var baseScore = (m.Importance * 2.0) + (double)m.Confidence + recencyBonus;

            if (queryEmbedding != null && queryEmbedding.Length > 0)
            {
                var memEmbedding = m.GetEmbedding();
                if (memEmbedding != null && memEmbedding.Length > 0)
                {
                    var sim = CosineSimilarityCalculator.Calculate(queryEmbedding, memEmbedding);
                    // Cosine similarity gives up to +12 bonus points (heavily boosting semantically aligned memories)
                    return baseScore + (sim * 12.0);
                }
            }

            return baseScore;
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

    public async Task<MemoryExtractionMetrics> StoreCandidatesAsync(
        Guid userId,
        Guid characterId,
        Guid? sessionId,
        IEnumerable<MemoryCandidate> candidates,
        CancellationToken ct = default)
    {
        if (userId == Guid.Empty || characterId == Guid.Empty)
        {
            return new MemoryExtractionMetrics(0, 0, 0, 0, 0);
        }

        var candidateList = candidates?.ToList() ?? new List<MemoryCandidate>();
        int extractedCount = candidateList.Count;
        if (extractedCount == 0)
        {
            return new MemoryExtractionMetrics(0, 0, 0, 0, 0);
        }

        int acceptedCount = 0;
        int rejectedCount = 0;
        int duplicateCount = 0;
        int persistedCount = 0;

        // 1. Filter valid candidates through IMemoryCandidateValidator
        var validCandidates = new List<MemoryCandidate>();
        foreach (var candidate in candidateList)
        {
            if (_validator.Validate(candidate, out var failureReason))
            {
                validCandidates.Add(candidate);
                acceptedCount++;
            }
            else
            {
                rejectedCount++;
                _logger.LogDebug("Memory candidate rejected during validation. Reason: {Reason}", failureReason);
            }
        }

        if (validCandidates.Count == 0)
        {
            return new MemoryExtractionMetrics(extractedCount, acceptedCount, rejectedCount, duplicateCount, persistedCount);
        }

        // 2. Query only existing memories matching candidate types
        var candidateTypes = validCandidates.Select(c => c.Type).Distinct().ToList();
        var existingMemories = await _unitOfWork.CharacterMemories.GetExistingByTypesAsync(
            userId,
            characterId,
            candidateTypes,
            ct: ct);

        // Map existing normalized content + type to memory for targeted deduplication
        var existingMap = new Dictionary<string, CharacterMemory>();
        foreach (var m in existingMemories)
        {
            var key = $"{m.Type}_{NormalizeContent(m.Content)}";
            existingMap[key] = m;
        }

        // 3. Process deduplication, vector embedding and persistence
        foreach (var c in validCandidates)
        {
            var normalized = NormalizeContent(c.Content);
            var key = $"{c.Type}_{normalized}";

            if (existingMap.TryGetValue(key, out var existing))
            {
                duplicateCount++;
                var newImportance = Math.Max(existing.Importance, c.Importance);
                var newConfidence = Math.Max(existing.Confidence, c.Confidence);
                existing.UpdateDetails(importance: newImportance, confidence: newConfidence);

                if (string.IsNullOrWhiteSpace(existing.EmbeddingJson))
                {
                    var emb = await _embeddingService.GenerateEmbeddingAsync(existing.Content, ct);
                    existing.SetEmbedding(emb);
                }

                _unitOfWork.CharacterMemories.Update(existing);
            }
            else
            {
                var emb = await _embeddingService.GenerateEmbeddingAsync(c.Content, ct);
                var newMemory = CharacterMemory.Create(
                    characterId: characterId,
                    userId: userId,
                    content: c.Content,
                    type: c.Type,
                    importance: c.Importance,
                    confidence: c.Confidence,
                    sourceSessionId: sessionId
                );
                newMemory.SetEmbedding(emb);

                await _unitOfWork.CharacterMemories.AddAsync(newMemory, ct);
                existingMap[key] = newMemory;
                persistedCount++;
            }
        }

        await _unitOfWork.SaveChangesAsync(ct);
        return new MemoryExtractionMetrics(extractedCount, acceptedCount, rejectedCount, duplicateCount, persistedCount);
    }
}
