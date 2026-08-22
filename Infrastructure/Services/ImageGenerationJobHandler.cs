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

        var generationRequestId = payload.ResolvedGenerationRequestId;
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("[SceneGenerationJobStarted] OutboxId={OutboxId}, SessionId={SessionId}, TurnId={TurnId}, Revision={Revision}, RequestId={RequestId}, WorkerId={WorkerId}",
            outboxId, snapshot.SessionId, snapshot.TurnId, snapshot.SceneRevision, generationRequestId, workerId);

        // 1. Application Idempotency Check: (SessionId, GenerationRequestId)
        var existingArtifact = await _dbContext.SceneImages
            .FirstOrDefaultAsync(img => img.SessionId == snapshot.SessionId && img.GenerationRequestId == generationRequestId, ct);

        if (existingArtifact != null)
        {
            _logger.LogInformation("[SceneGenerationSkipped] Artifact already exists for SessionId={SessionId}, RequestId={RequestId}. OutboxId={OutboxId}",
                snapshot.SessionId, generationRequestId, outboxId);
            return new JobExecutionResult(JobExecutionStatus.Skipped, "Artifact already exists");
        }

        // 2. Predecessor Gating & Late Predecessor Image URL Resolution
        string? resolvedPreviousSceneImageUrl = snapshot.PreviousSceneImageUrl;
        if (snapshot.SceneRevision > 1)
        {
            var predecessorArtifact = await _dbContext.SceneImages
                .Where(img => img.SessionId == snapshot.SessionId && img.SceneRevision == snapshot.SceneRevision - 1 && img.IsCurrent)
                .FirstOrDefaultAsync(ct);

            predecessorArtifact ??= await _dbContext.SceneImages
                .Where(img => img.SessionId == snapshot.SessionId && img.SceneRevision == snapshot.SceneRevision - 1)
                .OrderByDescending(img => img.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (predecessorArtifact != null)
            {
                resolvedPreviousSceneImageUrl = predecessorArtifact.ImageUrl;
            }
            else
            {
                var predJob = await _dbContext.ImageGenerationJobs
                    .Where(j => j.SessionId == snapshot.SessionId && j.SceneRevision == snapshot.SceneRevision - 1)
                    .OrderByDescending(j => j.CreatedAt)
                    .FirstOrDefaultAsync(ct);

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

        // 3. Lease-based Atomic Job Claim Before GPU Invocation
        var workflow = snapshot.GenerationProfile?.Workflow ?? "VisualIdentity";
        var workflowVersion = snapshot.GenerationProfile?.WorkflowVersion ?? 1;
        var leaseDuration = TimeSpan.FromMinutes(2);

        var job = await _dbContext.ImageGenerationJobs
            .FirstOrDefaultAsync(j => j.SessionId == snapshot.SessionId && j.GenerationRequestId == generationRequestId, ct);

        if (job == null)
        {
            job = new ImageGenerationJob(
                sessionId: snapshot.SessionId,
                turnId: snapshot.TurnId,
                characterId: snapshot.CharacterId,
                sceneRevision: snapshot.SceneRevision,
                generationRequestId: generationRequestId,
                provider: "ComfyUI",
                workflow: workflow,
                workflowVersion: workflowVersion
            );
            job.TryClaim(workerId, leaseDuration, now);

            try
            {
                await _dbContext.ImageGenerationJobs.AddAsync(job, ct);
                await _dbContext.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogInformation(ex, "[SceneGenerationRacePrevented] Concurrent worker inserted job for SessionId={SessionId}, RequestId={RequestId}",
                    snapshot.SessionId, generationRequestId);
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

            var claimed = job.TryClaim(workerId, leaseDuration, now);
            if (!claimed)
            {
                _logger.LogInformation("[SceneGenerationLeaseActive] Job is actively leased by worker '{ClaimedBy}' until {LeaseUntil}",
                    job.ClaimedBy, job.LeaseUntil);
                return new JobExecutionResult(JobExecutionStatus.Deferred, "Job actively leased by another worker");
            }

            await _dbContext.SaveChangesAsync(ct);
        }

        try
        {
            // 4. Deterministic prompt compilation purely from frozen VisualSnapshot
            var compiledPrompt = _visualCompiler.CompileScenePrompt(snapshot);
            var imageReq = ImageGenerationRequest.FromSnapshot(
                snapshot: snapshot,
                compiledPrompt: compiledPrompt,
                previousSceneImageUrlOverride: resolvedPreviousSceneImageUrl
            );

            // 5. Generate Image via configured Provider
            var genResult = await _imageService.GenerateImageWithResultAsync(imageReq, ct);

            // 6. Persist immutable SceneImage artifact & Update IsCurrent
            if (!string.IsNullOrWhiteSpace(genResult.ImageUrl))
            {
                // De-activate previous current images for this revision if regenerating
                var previousCurrents = await _dbContext.SceneImages
                    .Where(img => img.SessionId == snapshot.SessionId && img.SceneRevision == snapshot.SceneRevision && img.IsCurrent)
                    .ToListAsync(ct);

                foreach (var prev in previousCurrents)
                {
                    prev.SetCurrent(false);
                }

                var artifact = new SceneImage(
                    sessionId: snapshot.SessionId,
                    characterId: snapshot.CharacterId,
                    turnId: snapshot.TurnId,
                    sceneRevision: snapshot.SceneRevision,
                    imageUrl: genResult.ImageUrl,
                    prompt: compiledPrompt,
                    generationRequestId: generationRequestId,
                    identityReferenceUrl: snapshot.IdentityReferenceUrl,
                    previousSceneImageUrl: resolvedPreviousSceneImageUrl,
                    generationJobId: job.Id,
                    workflow: workflow,
                    workflowVersion: workflowVersion,
                    isCurrent: true
                );

                await _dbContext.SceneImages.AddAsync(artifact, ct);
                job.MarkCompleted(Clock.Now, genResult.MetadataJson);
                await _dbContext.SaveChangesAsync(ct);
            }

            stopwatch.Stop();
            _logger.LogInformation("[SceneGenerationCompleted] OutboxId={OutboxId}, JobId={JobId}, SessionId={SessionId}, Revision={Revision}, RequestId={RequestId}, LatencyMs={LatencyMs}",
                outboxId, job.Id, snapshot.SessionId, snapshot.SceneRevision, generationRequestId, stopwatch.ElapsedMilliseconds);

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
