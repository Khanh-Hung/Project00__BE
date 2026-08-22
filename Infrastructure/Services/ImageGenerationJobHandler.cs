using System.Diagnostics;
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

        // 1. Application Idempotency Check: (SessionId, TurnId, SceneRevision)
        var existingArtifact = await _dbContext.SceneImages
            .FirstOrDefaultAsync(img => img.SessionId == snapshot.SessionId && img.TurnId == snapshot.TurnId && img.SceneRevision == snapshot.SceneRevision, ct);

        if (existingArtifact != null)
        {
            _logger.LogInformation("[SceneGenerationSkipped] Artifact already exists for SessionId={SessionId}, TurnId={TurnId}, Revision={Revision}. OutboxId={OutboxId}",
                snapshot.SessionId, snapshot.TurnId, snapshot.SceneRevision, outboxId);
            return new JobExecutionResult(JobExecutionStatus.Skipped, "Artifact already exists");
        }

        // 2. Predecessor Gating based on Entities (SceneImage & ImageGenerationJob)
        if (snapshot.SceneRevision > 1)
        {
            var predecessorArtifact = await _dbContext.SceneImages
                .FirstOrDefaultAsync(img => img.SessionId == snapshot.SessionId && img.SceneRevision == snapshot.SceneRevision - 1, ct);

            if (predecessorArtifact == null)
            {
                var predJob = await _dbContext.ImageGenerationJobs
                    .OrderByDescending(j => j.CreatedAt)
                    .FirstOrDefaultAsync(j => j.SessionId == snapshot.SessionId && j.SceneRevision == snapshot.SceneRevision - 1, ct);

                if (predJob != null && predJob.Status == ImageJobStatus.Failed && !predJob.IsRetryable)
                {
                    _logger.LogWarning("[SceneGenerationFailed] Blocking Revision {Revision} because predecessor Revision {PredRev} failed permanently.",
                        snapshot.SceneRevision, snapshot.SceneRevision - 1);
                    throw new GpuNonTransientException($"Predecessor Revision {snapshot.SceneRevision - 1} failed permanently.");
                }

                // Check predecessor outbox message if job hasn't been created yet or failed at outbox level
                var predecessorMsg = await _dbContext.OutboxMessages
                    .Where(m => m.EventType == OutboxEventTypes.SceneImageGeneration && m.PayloadJson.Contains(snapshot.SessionId.ToString()))
                    .ToListAsync(ct);

                var predMatchingMsg = predecessorMsg.FirstOrDefault(m =>
                {
                    try
                    {
                        var p = System.Text.Json.JsonSerializer.Deserialize<SceneImageGenerationOutboxPayload>(m.PayloadJson);
                        return p?.Snapshot?.SceneRevision == snapshot.SceneRevision - 1;
                    }
                    catch { return false; }
                });

                if (predMatchingMsg != null && predMatchingMsg.Status == OutboxStatus.Failed)
                {
                    _logger.LogWarning("[SceneGenerationFailed] Blocking Revision {Revision} because predecessor Revision {PredRev} failed permanently in Outbox.",
                        snapshot.SceneRevision, snapshot.SceneRevision - 1);
                    throw new GpuNonTransientException($"Predecessor Revision {snapshot.SceneRevision - 1} failed permanently.");
                }

                _logger.LogInformation("[SceneGenerationDeferred] Deferring Revision {Revision} because predecessor Revision {PredRev} is not yet completed.",
                    snapshot.SceneRevision, snapshot.SceneRevision - 1);
                return new JobExecutionResult(JobExecutionStatus.Deferred, $"Predecessor Revision {snapshot.SceneRevision - 1} not yet completed");
            }
        }

        // 3. Atomic Job Claim Before GPU Invocation
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
            job.MarkProcessing(startedAt: Clock.Now);

            try
            {
                await _dbContext.ImageGenerationJobs.AddAsync(job, ct);
                await _dbContext.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogInformation(ex, "[SceneGenerationRacePrevented] Concurrent worker claimed job for SessionId={SessionId}, TurnId={TurnId}, Revision={Revision}",
                    snapshot.SessionId, snapshot.TurnId, snapshot.SceneRevision);
                return new JobExecutionResult(JobExecutionStatus.Skipped, "Job claimed by concurrent worker");
            }
        }
        else
        {
            if (job.Status == ImageJobStatus.Completed)
            {
                return new JobExecutionResult(JobExecutionStatus.Skipped, "Job already completed");
            }
            if (job.Status == ImageJobStatus.Failed && !job.IsRetryable)
            {
                throw new GpuNonTransientException(job.FailureReason ?? "Job permanently failed.");
            }

            job.MarkProcessing(startedAt: Clock.Now);
            await _dbContext.SaveChangesAsync(ct);
        }

        try
        {
            // 4. Deterministic prompt compilation purely from frozen VisualSnapshot
            var compiledPrompt = _visualCompiler.CompileScenePrompt(snapshot);
            var imageReq = ImageGenerationRequest.FromSnapshot(snapshot, compiledPrompt);

            // 5. Generate Image via configured Provider
            var genResult = await _imageService.GenerateImageWithResultAsync(imageReq, ct);

            // 6. Persist immutable SceneImage artifact & Complete Job
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
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[SceneGenerationCancelled] JobId={JobId} was cancelled.", job.Id);
            try
            {
                job.MarkCancelled(Clock.Now);
                await _dbContext.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to mark job as Cancelled for JobId={JobId}", job.Id);
            }
            throw;
        }
        catch (GpuNonTransientException ex)
        {
            job.MarkFailed(ex.Message, isRetryable: false, Clock.Now);
            await _dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (GpuTransientException ex)
        {
            job.MarkFailed(ex.Message, isRetryable: true, Clock.Now);
            await _dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            job.MarkFailed(ex.Message, isRetryable: true, Clock.Now);
            await _dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }
}
