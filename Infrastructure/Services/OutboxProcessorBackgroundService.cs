using Application.Common;
using Application.DTOs;
using Application.Exceptions;
using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace Infrastructure.Services;

public sealed class OutboxProcessorBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxProcessorBackgroundService> _logger;
    private readonly string _workerId;
    private static readonly object _inMemoryClaimLock = new();
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _leaseTimeout = TimeSpan.FromMinutes(2);

    public OutboxProcessorBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<OutboxProcessorBackgroundService> logger,
        string? workerId = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _workerId = workerId ?? $"worker-{Guid.NewGuid():N}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxProcessorBackgroundService started with WorkerId={WorkerId}.", _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingOutboxMessagesAsync(ct: stoppingToken);
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

    public Task<int> ProcessPendingOutboxMessagesAsync(CancellationToken ct = default)
        => ProcessPendingOutboxMessagesAsync(null, ct);

    public Task<int> ProcessDueMessagesAsync(DateTime? referenceTime = null, CancellationToken ct = default)
        => ProcessPendingOutboxMessagesAsync(referenceTime, ct);

    public async Task<int> ProcessPendingOutboxMessagesAsync(DateTime? referenceTime, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.ProjectDbContext>();
        var voiceCompiler = scope.ServiceProvider.GetRequiredService<IVoicePromptCompiler>();
        var visualCompiler = scope.ServiceProvider.GetRequiredService<IVisualPromptCompiler>();
        var voiceService = scope.ServiceProvider.GetRequiredService<IVoiceGenerationService>();
        var imageService = scope.ServiceProvider.GetRequiredService<IImageGenerationService>();
        var extractionTrigger = scope.ServiceProvider.GetRequiredService<IMemoryExtractionTrigger>();

        var now = referenceTime ?? Clock.Now;

        // 1. Crash Recovery: Reclaim stale processing leases (worker died or restarted)
        var staleCutoff = now - _leaseTimeout;
        var staleMessages = await dbContext.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Processing && m.ProcessingStartedAt != null && m.ProcessingStartedAt <= staleCutoff)
            .ToListAsync(ct);

        if (staleMessages.Count > 0)
        {
            _logger.LogWarning("Found {Count} stale processing outbox messages. Reclaiming back to Pending.", staleMessages.Count);
            foreach (var stale in staleMessages)
            {
                stale.ReclaimStaleProcessing(now);
            }
            await dbContext.SaveChangesAsync(ct);
        }

        // 2. Poll due Pending messages
        var pendingMessages = await dbContext.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Pending && (m.NextRetryAt == null || m.NextRetryAt <= now))
            .OrderBy(m => m.CreatedAt)
            .Take(10)
            .ToListAsync(ct);

        if (pendingMessages.Count == 0) return 0;

        _logger.LogInformation("Found {Count} due outbox messages to process.", pendingMessages.Count);

        int processedCount = 0;
        foreach (var msg in pendingMessages)
        {
            // 3. Atomic GPU Claim: Transition Pending -> Processing with ClaimedBy
            if (dbContext.Database.IsRelational())
            {
                var rowsClaimed = await dbContext.OutboxMessages
                    .Where(m => m.Id == msg.Id && m.Status == OutboxStatus.Pending)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.Status, OutboxStatus.Processing)
                        .SetProperty(m => m.ProcessingStartedAt, Clock.Now)
                        .SetProperty(m => m.ClaimedBy, _workerId)
                        .SetProperty(m => m.UpdatedAt, Clock.Now), ct);

                if (rowsClaimed == 0)
                {
                    // Lost race to another concurrent worker thread -> Skip without calling GPU
                    _logger.LogInformation("[SceneGenerationSkipped] Message {Id} was claimed by another worker. Skipping.", msg.Id);
                    continue;
                }

                await dbContext.Entry(msg).ReloadAsync(ct);
            }
            else
            {
                bool claimed = false;
                lock (_inMemoryClaimLock)
                {
                    if (msg.Status == OutboxStatus.Pending)
                    {
                        msg.MarkProcessing(_workerId, Clock.Now);
                        claimed = true;
                    }
                }

                if (!claimed)
                {
                    _logger.LogInformation("[SceneGenerationSkipped] Message {Id} was claimed by another worker. Skipping.", msg.Id);
                    continue;
                }

                await dbContext.SaveChangesAsync(ct);
            }

            var stopwatch = Stopwatch.StartNew();

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
                            var contextHash = VoiceContextHashCalculator.ComputeHash(voiceReq, voicePayload.Mood, voicePayload.MoodIntensity);

                            _logger.LogInformation("[VoiceGenerationStarted] OutboxId={OutboxId}, TurnId={TurnId}, ContextHash={Hash}, RetryCount={RetryCount}, WorkerId={WorkerId}",
                                msg.Id, voicePayload.TurnId, contextHash, msg.RetryCount, _workerId);

                            // A. Application Idempotency Check: ContextHash is primary idempotency identity
                            var existingArtifact = await dbContext.AudioArtifacts
                                .FirstOrDefaultAsync(a => a.ContextHash == contextHash, ct);

                            if (existingArtifact != null)
                            {
                                _logger.LogInformation("[VoiceGenerationSkipped] Artifact already exists with ContextHash={Hash}. OutboxId={OutboxId}",
                                    contextHash, msg.Id);
                                msg.MarkCompleted(Clock.Now);
                                break;
                            }

                            // B. Resolve Provider & Storage
                            var voiceProvider = scope.ServiceProvider.GetService<IVoiceProvider>();
                            var voiceStorage = scope.ServiceProvider.GetService<IVoiceStorage>();

                            string audioUrl;
                            string contentType = "audio/mpeg";
                            TimeSpan? duration = null;

                            if (voiceProvider != null && voiceStorage != null)
                            {
                                var providerResult = await voiceProvider.GenerateAudioAsync(voiceReq, ct);
                                var fileName = $"{voicePayload.TurnId:N}_{contextHash.Substring(0, 8)}.mp3";
                                audioUrl = await voiceStorage.SaveAudioAsync(providerResult.AudioBytes, fileName, providerResult.ContentType, ct);
                                contentType = providerResult.ContentType;
                                duration = providerResult.Duration;
                            }
                            else
                            {
                                var legacyResult = await voiceService.GenerateVoiceAsync(voiceReq, ct);
                                audioUrl = legacyResult.AudioUrl;
                                contentType = legacyResult.AudioFormat;
                                duration = legacyResult.Duration;
                            }

                            // C. Persist immutable AudioArtifact
                            if (!string.IsNullOrWhiteSpace(audioUrl))
                            {
                                var artifact = new AudioArtifact(
                                    sessionId: voicePayload.SessionId,
                                    characterId: voicePayload.CharacterId,
                                    turnId: voicePayload.TurnId,
                                    userId: voicePayload.UserId,
                                    voiceId: voiceReq.VoiceId,
                                    cleanedText: voiceReq.CleanedText,
                                    contextHash: contextHash,
                                    audioUrl: audioUrl,
                                    audioFormat: contentType,
                                    duration: duration
                                );

                                try
                                {
                                    await dbContext.AudioArtifacts.AddAsync(artifact, ct);
                                    await dbContext.SaveChangesAsync(ct);
                                }
                                catch (DbUpdateException ex)
                                {
                                    // Verify if collision is strictly due to duplicate ContextHash
                                    bool isUniqueCollision = false;
                                    if (ex.InnerException?.Message.Contains("IX_AudioArtifacts_ContextHash") == true
                                        || ex.Message.Contains("IX_AudioArtifacts_ContextHash")
                                        || (ex.InnerException is Npgsql.PostgresException pEx && pEx.SqlState == "23505")
                                        || (ex.InnerException?.GetType().Name == "SqliteException" && ex.InnerException.Message.Contains("UNIQUE constraint failed")))
                                    {
                                        isUniqueCollision = true;
                                    }
                                    else
                                    {
                                        // Provider-agnostic check: did another concurrent worker successfully commit this ContextHash?
                                        isUniqueCollision = await dbContext.AudioArtifacts.AsNoTracking().AnyAsync(a => a.ContextHash == contextHash, ct);
                                    }

                                    if (isUniqueCollision)
                                    {
                                        _logger.LogWarning(ex, "[VoiceGenerationDuplicateKeyHandled] Duplicate ContextHash={Hash} handled gracefully. OutboxId={OutboxId}",
                                            contextHash, msg.Id);
                                    }
                                    else
                                    {
                                        _logger.LogError(ex, "[VoiceGenerationDbError] Non-duplicate DB update failure on OutboxId={OutboxId}. Re-throwing.", msg.Id);
                                        throw;
                                    }
                                }
                            }

                            msg.MarkCompleted(Clock.Now);
                            stopwatch.Stop();
                            _logger.LogInformation("[VoiceGenerationCompleted] OutboxId={OutboxId}, TurnId={TurnId}, ContextHash={Hash}, LatencyMs={LatencyMs}",
                                msg.Id, voicePayload.TurnId, contextHash, stopwatch.ElapsedMilliseconds);
                        }
                        else
                        {
                            msg.MarkCompleted(Clock.Now);
                        }
                        break;

                    case OutboxEventTypes.SceneImageGeneration:
                        var scenePayload = JsonSerializer.Deserialize<SceneImageGenerationOutboxPayload>(msg.PayloadJson);
                        if (scenePayload?.Snapshot != null)
                        {
                            var dateTimeProvider = scope.ServiceProvider.GetService<IDateTimeProvider>() ?? new SystemDateTimeProvider();
                            var jobHandler = scope.ServiceProvider.GetService<IImageGenerationJobHandler>()
                                ?? new ImageGenerationJobHandler(dbContext, visualCompiler, imageService, Microsoft.Extensions.Logging.Abstractions.NullLogger<ImageGenerationJobHandler>.Instance, dateTimeProvider);

                            var result = await jobHandler.HandleSceneImageGenerationAsync(scenePayload, msg.Id, _workerId, now, ct);
                            if (result.Status == JobExecutionStatus.Deferred)
                            {
                                msg.MarkDeferred(now.AddSeconds(2));
                            }
                            else
                            {
                                msg.MarkCompleted(Clock.Now);
                            }
                        }
                        else
                        {
                            msg.MarkCompleted(Clock.Now);
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
                        msg.MarkCompleted(Clock.Now);
                        break;

                    default:
                        _logger.LogWarning("Unknown outbox event type '{EventType}' on message {Id}", msg.EventType, msg.Id);
                        msg.MarkCompleted(Clock.Now);
                        break;
                }
            }
            catch (VoiceNonTransientException ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "[VoiceGenerationFailed] Non-transient error on Outbox message {Id}. Fast-failing.", msg.Id);
                msg.MarkFailed(ex.Message, Clock.Now, isTransient: false);
            }
            catch (VoiceTransientException ex)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "[VoiceGenerationRetrying] Transient error on Outbox message {Id}. Scheduling retry with exponential backoff.", msg.Id);
                msg.MarkFailed(ex.Message, Clock.Now, isTransient: true);
            }
            catch (GpuNonTransientException ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "[SceneGenerationFailed] Non-transient error on Outbox message {Id}. Fast-failing.", msg.Id);
                msg.MarkFailed(ex.Message, Clock.Now, isTransient: false);
            }
            catch (GpuTransientException ex)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "[SceneGenerationRetrying] Transient error on Outbox message {Id}. Scheduling retry with exponential backoff.", msg.Id);
                msg.MarkFailed(ex.Message, Clock.Now, isTransient: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "Unexpected exception processing outbox message {Id}.", msg.Id);
                msg.MarkFailed(ex.Message, Clock.Now, isTransient: true);
            }

            await dbContext.SaveChangesAsync(ct);
            processedCount++;
        }

        return processedCount;
    }
}
