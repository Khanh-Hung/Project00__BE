using System.Text.Json;
using System.Threading.Channels;
using Application.Abstractions.Data;
using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.LLM.Core;
using Infrastructure.LLM.Prompts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class MemoryExtractionBackgroundService : BackgroundService, IMemoryExtractionTrigger
{
    private readonly Channel<MemoryExtractionJob> _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MemoryExtractionBackgroundService> _logger;

    public MemoryExtractionBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<MemoryExtractionBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        // Bounded channel to prevent unbounded memory growth
        var options = new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        };
        _queue = Channel.CreateBounded<MemoryExtractionJob>(options);
    }

    public bool NotifyMessageSent(MemoryExtractionJob job)
    {
        if (job == null || job.UserId == Guid.Empty || job.CharacterId == Guid.Empty)
        {
            return false;
        }

        // Trigger extraction policy: When conversation has enough messages (e.g. at least 4 turns or batch)
        return _queue.Writer.TryWrite(job);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MemoryExtractionBackgroundService is starting...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await _queue.Reader.ReadAsync(stoppingToken);
                await ProcessExtractionJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in memory extraction worker loop");
            }
        }

        _logger.LogInformation("MemoryExtractionBackgroundService stopped.");
    }

    private async Task ProcessExtractionJobAsync(MemoryExtractionJob job, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var geminiClient = scope.ServiceProvider.GetRequiredService<GeminiApiClient>();

        var characterRepo = unitOfWork.GetRepository<Character>();
        var character = await characterRepo.GetByIdAsync(job.CharacterId, ct);
        if (character == null)
        {
            _logger.LogWarning("Character {CharacterId} not found for extraction job", job.CharacterId);
            return;
        }

        // Format recent conversation window (up to last 10 messages)
        var recentExcerpts = job.RecentMessages.TakeLast(10).ToList();
        if (recentExcerpts.Count < 2)
        {
            return;
        }

        var conversationText = string.Join("\n", recentExcerpts.Select(m => $"{m.Role}: {m.Content}"));
        var systemPrompt = MemoryExtractionPrompts.BuildExtractionSystemPrompt(character);

        var contents = new List<object>
        {
            new
            {
                role = "user",
                parts = new[] { new { text = $"Here is the recent conversation excerpt:\n\n{conversationText}\n\nExtract 0 to 2 memory candidates if applicable." } }
            }
        };

        try
        {
            var rawJson = await geminiClient.GenerateTextAsync(
                systemPrompt: systemPrompt,
                contents: contents,
                temperature: 0.2, // Low temperature for factual precision
                maxOutputTokens: 500,
                ct: ct
            );

            var candidates = ParseExtractionResult(rawJson);
            if (candidates.Count > 0)
            {
                var stored = await memoryService.StoreCandidatesAsync(
                    job.UserId,
                    job.CharacterId,
                    job.SessionId,
                    candidates,
                    ct
                );
                _logger.LogInformation("Extracted and stored {StoredCount} memories for User {UserId} and Character {CharacterName}",
                    stored, job.UserId, character.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory extraction failed for User {UserId} / Character {CharacterId}. Chat execution remains unaffected.",
                job.UserId, job.CharacterId);
        }
    }

    private List<MemoryCandidate> ParseExtractionResult(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return [];

        try
        {
            var cleanJson = rawText.Trim();
            if (cleanJson.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
                cleanJson = cleanJson.Substring(7);
            if (cleanJson.StartsWith("```"))
                cleanJson = cleanJson.Substring(3);
            if (cleanJson.EndsWith("```"))
                cleanJson = cleanJson.Substring(0, cleanJson.Length - 3);
            cleanJson = cleanJson.Trim();

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<MemoryExtractionResult>(cleanJson, options);
            return result?.Memories ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed deserializing extraction result JSON: {Raw}", rawText);
            return [];
        }
    }
}
