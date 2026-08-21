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
                                    // Unique constraint race condition safety net (another concurrent worker committed exact same ContextHash)
                                    _logger.LogWarning(ex, "[VoiceGenerationDuplicateKeyHandled] Duplicate ContextHash={Hash} handled gracefully. OutboxId={OutboxId}",
                                        contextHash, msg.Id);
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
                            var snapshot = scenePayload.Snapshot;
                            _logger.LogInformation("[SceneGenerationStarted] OutboxId={OutboxId}, SessionId={SessionId}, TurnId={TurnId}, Revision={Revision}, RetryCount={RetryCount}, WorkerId={WorkerId}",
                                msg.Id, snapshot.SessionId, snapshot.TurnId, snapshot.SceneRevision, msg.RetryCount, _workerId);

                            // A. Application Idempotency: Check if SceneImage artifact already exists
                            var existingArtifact = await dbContext.SceneImages
                                .FirstOrDefaultAsync(img => img.SessionId == snapshot.SessionId && img.SceneRevision == snapshot.SceneRevision, ct);

                            if (existingArtifact != null)
                            {
                                _logger.LogInformation("[SceneGenerationSkipped] Artifact already exists for SessionId={SessionId}, Revision={Revision}. OutboxId={OutboxId}",
                                    snapshot.SessionId, snapshot.SceneRevision, msg.Id);
                                msg.MarkCompleted(Clock.Now);
                                break;
                            }

                            // B. Per-Session Predecessor Gating: Ensure Revision N - 1 artifact is completed
                            if (snapshot.SceneRevision > 1)
                            {
                                var predecessorArtifact = await dbContext.SceneImages
                                    .FirstOrDefaultAsync(img => img.SessionId == snapshot.SessionId && img.SceneRevision == snapshot.SceneRevision - 1, ct);

                                if (predecessorArtifact == null)
                                {
                                    // Check outbox status of Revision N - 1
                                    var predecessorMsg = await dbContext.OutboxMessages
                                        .Where(m => m.EventType == OutboxEventTypes.SceneImageGeneration && m.PayloadJson.Contains(snapshot.SessionId.ToString()))
                                        .ToListAsync(ct);

                                    var predMatchingMsg = predecessorMsg.FirstOrDefault(m =>
                                    {
                                        try
                                        {
                                            var payload = JsonSerializer.Deserialize<SceneImageGenerationOutboxPayload>(m.PayloadJson);
                                            return payload?.Snapshot?.SceneRevision == snapshot.SceneRevision - 1;
                                        }
                                        catch { return false; }
                                    });

                                    if (predMatchingMsg != null && predMatchingMsg.Status == OutboxStatus.Failed)
                                    {
                                        // Predecessor failed permanently -> Block Revision N with clear reason (No infinite deadlock)
                                        _logger.LogWarning("[SceneGenerationFailed] Blocking Revision {Revision} because predecessor Revision {PredRev} failed permanently. OutboxId={OutboxId}",
                                            snapshot.SceneRevision, snapshot.SceneRevision - 1, msg.Id);
                                        msg.MarkFailed($"Predecessor Revision {snapshot.SceneRevision - 1} failed permanently.", Clock.Now, isTransient: false);
                                        break;
                                    }
                                    else
                                    {
                                        // Predecessor is still Pending/Processing -> Defer Revision N without incrementing retry count
                                        _logger.LogInformation("[SceneGenerationDeferred] Deferring Revision {Revision} because predecessor Revision {PredRev} is not yet completed. OutboxId={OutboxId}",
                                            snapshot.SceneRevision, snapshot.SceneRevision - 1, msg.Id);
                                        msg.MarkDeferred(now.AddSeconds(2));
                                        break;
                                    }
                                }
                            }

                            // C. Deterministic prompt compilation purely from VisualSnapshot
                            var compiledPrompt = visualCompiler.CompileScenePrompt(snapshot);
                            var imageReq = new ImageGenerationRequest(
                                Prompt: compiledPrompt,
                                ReferenceImageUrl: snapshot.IdentityReferenceUrl,
                                PreviousSceneImageUrl: snapshot.PreviousSceneImageUrl
                            );

                            var generatedImageUrl = await imageService.GenerateImageAsync(imageReq, ct);

                            // D. Persist immutable SceneImage artifact
                            if (!string.IsNullOrWhiteSpace(generatedImageUrl))
                            {
                                try
                                {
                                    var artifact = new SceneImage(
                                        sessionId: snapshot.SessionId,
                                        characterId: snapshot.CharacterId,
                                        turnId: snapshot.TurnId,
                                        sceneRevision: snapshot.SceneRevision,
                                        imageUrl: generatedImageUrl,
                                        prompt: compiledPrompt,
                                        identityReferenceUrl: snapshot.IdentityReferenceUrl,
                                        previousSceneImageUrl: snapshot.PreviousSceneImageUrl
                                    );
                                    await dbContext.SceneImages.AddAsync(artifact, ct);
                                    await dbContext.SaveChangesAsync(ct);
                                }
                                catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("IX_SceneImages_SessionId_SceneRevision") == true || ex.Message.Contains("IX_SceneImages_SessionId_SceneRevision"))
                                {
                                    // DB Unique Constraint safety net
                                    _logger.LogInformation("[SceneGenerationSkipped] Concurrent race caught by DB Unique Constraint for SessionId={SessionId}, Revision={Revision}",
                                        snapshot.SessionId, snapshot.SceneRevision);
                                }
                            }

                            msg.MarkCompleted(Clock.Now);
                            stopwatch.Stop();
                            _logger.LogInformation("[SceneGenerationCompleted] OutboxId={OutboxId}, SessionId={SessionId}, Revision={Revision}, LatencyMs={LatencyMs}",
                                msg.Id, snapshot.SessionId, snapshot.SceneRevision, stopwatch.ElapsedMilliseconds);
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
