using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Infrastructure.Services;

public sealed class OutboxProcessorBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxProcessorBackgroundService> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);

    public OutboxProcessorBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<OutboxProcessorBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxProcessorBackgroundService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingOutboxMessagesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error occurred during Outbox processing cycle.");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("OutboxProcessorBackgroundService stopped.");
    }

    public async Task<int> ProcessPendingOutboxMessagesAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.ProjectDbContext>();
        var voiceCompiler = scope.ServiceProvider.GetRequiredService<IVoicePromptCompiler>();
        var visualCompiler = scope.ServiceProvider.GetRequiredService<IVisualPromptCompiler>();
        var voiceService = scope.ServiceProvider.GetRequiredService<IVoiceGenerationService>();
        var imageService = scope.ServiceProvider.GetRequiredService<IImageGenerationService>();
        var extractionTrigger = scope.ServiceProvider.GetRequiredService<IMemoryExtractionTrigger>();

        var pendingMessages = await dbContext.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Pending)
            .OrderBy(m => m.CreatedAt)
            .Take(10)
            .ToListAsync(ct);

        if (pendingMessages.Count == 0) return 0;

        _logger.LogInformation("Found {Count} pending outbox messages to process.", pendingMessages.Count);

        int processedCount = 0;
        foreach (var msg in pendingMessages)
        {
            msg.MarkProcessing();
            await dbContext.SaveChangesAsync(ct);

            try
            {
                switch (msg.EventType)
                {
                    case OutboxEventTypes.VoiceGeneration:
                        var voicePayload = JsonSerializer.Deserialize<VoiceGenerationOutboxPayload>(msg.PayloadJson);
                        if (voicePayload != null)
                        {
                            var voiceContext = new VoiceContext(
                                Voice: voicePayload.VoiceProfile,
                                Mood: voicePayload.Mood,
                                MoodIntensity: voicePayload.MoodIntensity,
                                AffectionScore: voicePayload.AffectionScore,
                                RelationshipStage: voicePayload.RelationshipStage,
                                RawText: voicePayload.RawText
                            );
                            var voiceReq = voiceCompiler.CompileVoiceRequest(voiceContext);
                            await voiceService.GenerateVoiceAsync(voiceReq, ct);
                        }
                        break;

                    case OutboxEventTypes.SceneImageGeneration:
                        var scenePayload = JsonSerializer.Deserialize<SceneImageGenerationOutboxPayload>(msg.PayloadJson);
                        if (scenePayload?.Snapshot != null)
                        {
                            // Invariant: Prompt is deterministically compiled from the frozen VisualSnapshot
                            var compiledPrompt = visualCompiler.CompileScenePrompt(scenePayload.Snapshot);
                            var imageReq = new ImageGenerationRequest(
                                Prompt: compiledPrompt,
                                ReferenceImageUrl: scenePayload.Snapshot.IdentityReferenceUrl,
                                PreviousSceneImageUrl: scenePayload.Snapshot.PreviousSceneImageUrl
                            );
                            await imageService.GenerateImageAsync(imageReq, ct);
                        }
                        break;

                    case OutboxEventTypes.MemoryExtraction:
                        var memoryPayload = JsonSerializer.Deserialize<MemoryExtractionOutboxPayload>(msg.PayloadJson);
                        if (memoryPayload != null)
                        {
                            var extractionJob = new MemoryExtractionJob(
                                SessionId: memoryPayload.SessionId,
                                CharacterId: memoryPayload.CharacterId,
                                UserId: memoryPayload.UserId,
                                RecentMessages: memoryPayload.RecentMessages.ToList(),
                                UserMessageCount: memoryPayload.UserMessageCount
                            );
                            extractionTrigger.NotifyMessageSent(extractionJob);
                        }
                        break;

                    default:
                        _logger.LogWarning("Unknown outbox event type '{EventType}' on message {Id}", msg.EventType, msg.Id);
                        break;
                }

                msg.MarkCompleted(DateTime.UtcNow);
                processedCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to process outbox message {Id} of type {EventType}", msg.Id, msg.EventType);
                msg.MarkFailed(ex.Message, DateTime.UtcNow);
            }

            await dbContext.SaveChangesAsync(ct);
        }

        return processedCount;
    }
}
