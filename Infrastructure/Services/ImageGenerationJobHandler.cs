using System.Diagnostics;
using System.Text.Json;
using Application.DTOs;
using Application.Exceptions;
using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class ImageGenerationJobHandler : IImageGenerationJobHandler
{
    private readonly ProjectDbContext _dbContext;
    private readonly IVisualPromptCompiler _visualCompiler;
    private readonly IImageGenerationService _imageService;
    private readonly ILogger<ImageGenerationJobHandler> _logger;

    public ImageGenerationJobHandler(
        ProjectDbContext dbContext,
        IVisualPromptCompiler visualCompiler,
        IImageGenerationService imageService,
        ILogger<ImageGenerationJobHandler> logger)
    {
        _dbContext = dbContext;
        _visualCompiler = visualCompiler;
        _imageService = imageService;
        _logger = logger;
    }

    public async Task<JobExecutionResult> HandleSceneImageGenerationAsync(
        SceneImageGenerationOutboxPayload payload,
        Guid outboxId,
        string workerId,
        DateTime now,
        CancellationToken ct = default)
    {
        var snapshot = payload.Snapshot;
        if (snapshot == null)
        {
            _logger.LogWarning("Scene image generation payload has null VisualSnapshot. OutboxId={OutboxId}", outboxId);
            return new JobExecutionResult(JobExecutionStatus.Skipped, "Null snapshot");
        }

        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("[SceneGenerationJobStarted] OutboxId={OutboxId}, SessionId={SessionId}, TurnId={TurnId}, Revision={Revision}, WorkerId={WorkerId}",
            outboxId, snapshot.SessionId, snapshot.TurnId, snapshot.SceneRevision, workerId);

        // 1. Application Idempotency Check: (SessionId, TurnId, SceneRevision) or (SessionId, SceneRevision)
        var existingArtifact = await _dbContext.SceneImages
            .FirstOrDefaultAsync(img => img.SessionId == snapshot.SessionId && img.TurnId == snapshot.TurnId && img.SceneRevision == snapshot.SceneRevision, ct)
            ?? await _dbContext.SceneImages
            .FirstOrDefaultAsync(img => img.SessionId == snapshot.SessionId && img.SceneRevision == snapshot.SceneRevision, ct);

        if (existingArtifact != null)
        {
            _logger.LogInformation("[SceneGenerationSkipped] Artifact already exists for SessionId={SessionId}, Revision={Revision}. OutboxId={OutboxId}",
                snapshot.SessionId, snapshot.SceneRevision, outboxId);
            return new JobExecutionResult(JobExecutionStatus.Skipped, "Artifact already exists");
        }

        // 2. Per-Session Predecessor Gating: Ensure Revision N - 1 is completed
        if (snapshot.SceneRevision > 1)
        {
            var predecessorArtifact = await _dbContext.SceneImages
                .FirstOrDefaultAsync(img => img.SessionId == snapshot.SessionId && img.SceneRevision == snapshot.SceneRevision - 1, ct);

            if (predecessorArtifact == null)
            {
                var predecessorMsg = await _dbContext.OutboxMessages
                    .Where(m => m.EventType == OutboxEventTypes.SceneImageGeneration && m.PayloadJson.Contains(snapshot.SessionId.ToString()))
                    .ToListAsync(ct);

                var predMatchingMsg = predecessorMsg.FirstOrDefault(m =>
                {
                    try
                    {
                        var p = JsonSerializer.Deserialize<SceneImageGenerationOutboxPayload>(m.PayloadJson);
                        return p?.Snapshot?.SceneRevision == snapshot.SceneRevision - 1;
                    }
                    catch { return false; }
                });

                if (predMatchingMsg != null && predMatchingMsg.Status == OutboxStatus.Failed)
                {
                    _logger.LogWarning("[SceneGenerationFailed] Blocking Revision {Revision} because predecessor Revision {PredRev} failed permanently.",
                        snapshot.SceneRevision, snapshot.SceneRevision - 1);
                    throw new GpuNonTransientException($"Predecessor Revision {snapshot.SceneRevision - 1} failed permanently.");
                }
                else
                {
                    _logger.LogInformation("[SceneGenerationDeferred] Deferring Revision {Revision} because predecessor Revision {PredRev} is not yet completed.",
                        snapshot.SceneRevision, snapshot.SceneRevision - 1);
                    return new JobExecutionResult(JobExecutionStatus.Deferred, $"Predecessor Revision {snapshot.SceneRevision - 1} not yet completed");
                }
            }
        }

        // 3. Create or Track ImageGenerationJob
        var workflow = snapshot.GenerationProfile?.Workflow ?? "VisualIdentity";
        var workflowVersion = snapshot.GenerationProfile?.WorkflowVersion ?? 1;

        var job = await _dbContext.ImageGenerationJobs
            .FirstOrDefaultAsync(j => j.SessionId == snapshot.SessionId && j.TurnId == snapshot.TurnId && j.SceneRevision == snapshot.SceneRevision, ct);

        if (job == null)
        {
            job = new ImageGenerationJob(
                sessionId: snapshot.SessionId,
                turnId: snapshot.TurnId,
                characterId: snapshot.CharacterId,
                sceneRevision: snapshot.SceneRevision,
                provider: "ComfyUI",
                workflow: workflow,
                workflowVersion: workflowVersion
            );
            await _dbContext.ImageGenerationJobs.AddAsync(job, ct);
        }

        job.MarkProcessing(startedAt: Clock.Now);
        await _dbContext.SaveChangesAsync(ct);

        try
        {
            // 4. Deterministic prompt compilation purely from frozen VisualSnapshot
            var compiledPrompt = _visualCompiler.CompileScenePrompt(snapshot);
            var imageReq = ImageGenerationRequest.FromSnapshot(snapshot, compiledPrompt);

            // 5. Generate Image via configured Provider
            var genResult = await _imageService.GenerateImageWithResultAsync(imageReq, ct);

            // 6. Persist immutable SceneImage artifact
            if (!string.IsNullOrWhiteSpace(genResult.ImageUrl))
            {
                try
                {
                    var artifact = new SceneImage(
                        sessionId: snapshot.SessionId,
                        characterId: snapshot.CharacterId,
                        turnId: snapshot.TurnId,
                        sceneRevision: snapshot.SceneRevision,
                        imageUrl: genResult.ImageUrl,
                        prompt: compiledPrompt,
                        identityReferenceUrl: snapshot.IdentityReferenceUrl,
                        previousSceneImageUrl: snapshot.PreviousSceneImageUrl,
                        generationJobId: job.Id,
                        workflow: workflow,
                        workflowVersion: workflowVersion
                    );
                    await _dbContext.SceneImages.AddAsync(artifact, ct);
                    job.MarkCompleted(Clock.Now, genResult.MetadataJson);
                    await _dbContext.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("IX_SceneImages_SessionId_SceneRevision") == true || ex.Message.Contains("IX_SceneImages_SessionId_SceneRevision"))
                {
                    _logger.LogInformation("[SceneGenerationSkipped] Concurrent race caught by DB Unique Constraint for SessionId={SessionId}, Revision={Revision}",
                        snapshot.SessionId, snapshot.SceneRevision);
                }
            }

            stopwatch.Stop();
            _logger.LogInformation("[SceneGenerationCompleted] OutboxId={OutboxId}, JobId={JobId}, SessionId={SessionId}, Revision={Revision}, LatencyMs={LatencyMs}",
                outboxId, job.Id, snapshot.SessionId, snapshot.SceneRevision, stopwatch.ElapsedMilliseconds);

            return new JobExecutionResult(JobExecutionStatus.Completed);
        }
        catch (GpuNonTransientException ex)
        {
            job.MarkFailed(ex.Message, isRetryable: false, Clock.Now);
            await _dbContext.SaveChangesAsync(ct);
            throw;
        }
        catch (GpuTransientException ex)
        {
            job.MarkFailed(ex.Message, isRetryable: true, Clock.Now);
            await _dbContext.SaveChangesAsync(ct);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            job.MarkFailed(ex.Message, isRetryable: true, Clock.Now);
            await _dbContext.SaveChangesAsync(ct);
            throw;
        }
    }
}
