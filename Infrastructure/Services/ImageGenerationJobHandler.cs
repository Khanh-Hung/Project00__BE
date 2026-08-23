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
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILogger<ImageGenerationJobHandler> _logger;

    public ImageGenerationJobHandler(
        ProjectDbContext dbContext,
        IVisualPromptCompiler visualCompiler,
        IImageGenerationService imageService,
        ILogger<ImageGenerationJobHandler> logger,
        IDateTimeProvider dateTimeProvider)
    {
        _dbContext = dbContext;
        _visualCompiler = visualCompiler;
        _imageService = imageService;
        _logger = logger;
        _dateTimeProvider = dateTimeProvider;
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

        var generationRequestId = payload.GenerationRequestId;
        if (generationRequestId == Guid.Empty)
        {
            throw new GpuNonTransientException("GenerationRequestId is required and cannot be Guid.Empty.");
        }
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
            var predRev = snapshot.PredecessorSceneRevision ?? (snapshot.SceneRevision - 1);
            var predecessorArtifact = await _dbContext.SceneImages
                .FirstOrDefaultAsync(img => img.SessionId == snapshot.SessionId && img.SceneRevision == predRev && img.IsCurrent, ct);

            if (predecessorArtifact != null)
            {
                resolvedPreviousSceneImageUrl = predecessorArtifact.ImageUrl;
            }
            else
            {
                var predJob = await _dbContext.ImageGenerationJobs
                    .Where(j => j.SessionId == snapshot.SessionId && j.SceneRevision == predRev)
                    .OrderByDescending(j => j.CreatedAt)
                    .FirstOrDefaultAsync(ct);

                if (predJob != null && predJob.Status == ImageJobStatus.Failed && !predJob.IsRetryable)
                {
                    _logger.LogWarning("[SceneGenerationFailed] Blocking Revision {Revision} because predecessor Revision {PredRev} failed permanently.",
                        snapshot.SceneRevision, predRev);
                    throw new GpuNonTransientException($"Predecessor Revision {predRev} failed permanently.");
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
                        return p?.Snapshot?.SceneRevision == predRev;
                    }
                    catch { return false; }
                });

                if (predMatchingMsg != null && predMatchingMsg.Status == OutboxStatus.Failed)
                {
                    _logger.LogWarning("[SceneGenerationFailed] Blocking Revision {Revision} because predecessor Revision {PredRev} failed permanently in Outbox.",
                        snapshot.SceneRevision, predRev);
                    throw new GpuNonTransientException($"Predecessor Revision {predRev} failed permanently.");
                }

                _logger.LogInformation("[SceneGenerationDeferred] Deferring Revision {Revision} because predecessor Revision {PredRev} has no active current artifact yet.",
                    snapshot.SceneRevision, predRev);
                return new JobExecutionResult(JobExecutionStatus.Deferred, $"Predecessor Revision {predRev} not yet completed");
            }
        }

        // 3. Lease-based Atomic Job Claim Before GPU Invocation
        var workflow = snapshot.GenerationProfile?.Workflow ?? "VisualIdentity";
        var workflowVersion = snapshot.GenerationProfile?.WorkflowVersion ?? 1;
        var leaseDuration = TimeSpan.FromMinutes(4); // 240s: provides 120s safety margin over max 120s ComfyUI timeout

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
                return new JobExecutionResult(JobExecutionStatus.Deferred, "Job claimed by concurrent worker");
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

            try
            {
                await _dbContext.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogInformation(ex, "[SceneGenerationRacePrevented] Concurrent worker updated lease for SessionId={SessionId}, RequestId={RequestId}",
                    snapshot.SessionId, generationRequestId);
                return new JobExecutionResult(JobExecutionStatus.Deferred, "Job lease claimed by concurrent worker");
            }
        }

        try
        {
            // 4. Deterministic prompt compilation purely from frozen VisualSnapshot
            var compiledPrompt = _visualCompiler.CompileScenePrompt(snapshot);
            var imageReq = ImageGenerationRequest.FromSnapshot(
                snapshot: snapshot,
                compiledPrompt: compiledPrompt,
                previousSceneImageUrlOverride: resolvedPreviousSceneImageUrl,
                providerJobId: job.ProviderJobId,
                onPromptQueuedAsync: async (promptId, token) =>
                {
                    job.SetProviderJobId(promptId);
                    await _dbContext.SaveChangesAsync(token);
                }
            );

            // 5. Generate Image via configured Provider
            var genResult = await _imageService.GenerateImageWithResultAsync(imageReq, ct);

            // Strict Validation: ImageUrl must be non-empty; never mark Job Completed without an artifact!
            if (string.IsNullOrWhiteSpace(genResult.ImageUrl))
            {
                _logger.LogError("[SceneGenerationFailed] Provider returned empty ImageUrl for JobId={JobId}.", job.Id);
                throw new GpuNonTransientException("Image generation completed without producing an image URL.");
            }

            // 6. Strict Atomic DB Conditional Fencing & Artifact Persistence in ONE Unit of Work
            var liveUtc = _dateTimeProvider.UtcNow;

            if (_dbContext.Database.IsRelational())
            {
                try
                {
                    await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);

                    // Step 6a: Conditional update as atomic fencing gate
                    var rowsAffected = await _dbContext.ImageGenerationJobs
                        .Where(j => j.Id == job.Id 
                                    && j.ClaimedBy == workerId 
                                    && j.Version == job.Version 
                                    && j.Status == ImageJobStatus.Processing 
                                    && j.LeaseUntil.HasValue 
                                    && j.LeaseUntil.Value > liveUtc)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(j => j.Status, ImageJobStatus.Completed)
                            .SetProperty(j => j.CompletedAt, liveUtc)
                            .SetProperty(j => j.GenerationMetadataJson, genResult.MetadataJson)
                            .SetProperty(j => j.Version, j => j.Version + 1)
                            .SetProperty(j => j.UpdatedAt, liveUtc), ct);

                    if (rowsAffected != 1)
                    {
                        _logger.LogWarning("[SceneGenerationStaleDiscarded] Atomic conditional update fencing failed for JobId={JobId}, WorkerId={WorkerId}. Rows affected: {Rows}. Discarding artifact.",
                            job.Id, workerId, rowsAffected);
                        await transaction.RollbackAsync(ct);
                        return new JobExecutionResult(JobExecutionStatus.Deferred, "Worker lease lost or expired before conditional update");
                    }

                    // Step 6b: Deactivate previous currents for this revision within the SAME transaction
                    await _dbContext.SceneImages
                        .Where(img => img.SessionId == snapshot.SessionId && img.SceneRevision == snapshot.SceneRevision && img.IsCurrent)
                        .ExecuteUpdateAsync(s => s.SetProperty(img => img.IsCurrent, false), ct);

                    // Step 6c: Insert new SceneImage artifact within the SAME transaction
                    var artifact = new SceneImage(
                        sessionId: snapshot.SessionId,
                        characterId: snapshot.CharacterId,
                        turnId: snapshot.TurnId,
                        sceneRevision: snapshot.SceneRevision,
                        imageUrl: genResult.ImageUrl,
                        prompt: compiledPrompt,
                        generationRequestId: generationRequestId,
                        generationJobId: job.Id,
                        identityReferenceUrl: snapshot.IdentityReferenceUrl,
                        previousSceneImageUrl: resolvedPreviousSceneImageUrl,
                        workflow: workflow,
                        workflowVersion: workflowVersion,
                        isCurrent: true
                    );

                    await _dbContext.SceneImages.AddAsync(artifact, ct);
                    await _dbContext.SaveChangesAsync(ct);

                    await transaction.CommitAsync(ct);
                }
                catch (DbUpdateException ex)
                {
                    bool isTransient = DbExceptionClassifier.IsTransient(ex);
                    if (isTransient)
                    {
                        _logger.LogWarning(ex, "[SceneGenerationConcurrencyConflict] Relational transaction conflict during artifact commit for JobId={JobId}, WorkerId={WorkerId}. Discarding artifact.",
                            job.Id, workerId);
                        return new JobExecutionResult(JobExecutionStatus.Deferred, "Relational transaction conflict during artifact commit");
                    }
                    _logger.LogError(ex, "[SceneGenerationPermanentDbError] Permanent database update failure during artifact commit for JobId={JobId}, WorkerId={WorkerId}.", job.Id, workerId);
                    throw new GpuNonTransientException($"Permanent database error: {ex.Message}", statusCode: null, innerException: ex);
                }
                catch (System.Data.Common.DbException ex)
                {
                    bool isTransient = DbExceptionClassifier.IsTransient(ex);
                    if (isTransient)
                    {
                        _logger.LogWarning(ex, "[SceneGenerationTransactionBusy] Database connection/transaction conflict for JobId={JobId}, WorkerId={WorkerId}. Discarding artifact.",
                            job.Id, workerId);
                        return new JobExecutionResult(JobExecutionStatus.Deferred, "Database transaction conflict during artifact commit");
                    }
                    _logger.LogError(ex, "[SceneGenerationPermanentDbError] Permanent database connection failure for JobId={JobId}, WorkerId={WorkerId}.", job.Id, workerId);
                    throw new GpuNonTransientException($"Permanent database error: {ex.Message}", statusCode: null, innerException: ex);
                }
            }
            else
            {
                var currentJob = await _dbContext.ImageGenerationJobs
                    .AsNoTracking()
                    .FirstOrDefaultAsync(j => j.Id == job.Id, ct);

                if (currentJob == null || 
                    currentJob.ClaimedBy != workerId || 
                    !currentJob.LeaseUntil.HasValue || 
                    currentJob.LeaseUntil.Value <= liveUtc || 
                    currentJob.Version != job.Version ||
                    currentJob.Status != ImageJobStatus.Processing)
                {
                    _logger.LogWarning("[SceneGenerationStaleDiscarded] Stale worker '{WorkerId}' finished after lease lost/expired for JobId={JobId} (CurrentOwner='{ClaimedBy}', CurrentVersion={CurrentVer}, ExpectedVersion={ExpectedVer}). Discarding artifact.",
                        workerId, job.Id, currentJob?.ClaimedBy, currentJob?.Version, job.Version);
                    return new JobExecutionResult(JobExecutionStatus.Deferred, "Worker lease lost or expired before commit");
                }

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
                    generationJobId: job.Id,
                    identityReferenceUrl: snapshot.IdentityReferenceUrl,
                    previousSceneImageUrl: resolvedPreviousSceneImageUrl,
                    workflow: workflow,
                    workflowVersion: workflowVersion,
                    isCurrent: true
                );

                try
                {
                    await _dbContext.SceneImages.AddAsync(artifact, ct);
                    job.MarkCompleted(liveUtc, genResult.MetadataJson);
                    await _dbContext.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException ex)
                {
                    _logger.LogWarning(ex, "[SceneGenerationStaleDiscarded] Concurrency conflict during artifact commit for JobId={JobId}, WorkerId={WorkerId}. Another worker modified/reclaimed the job.",
                        job.Id, workerId);
                    return new JobExecutionResult(JobExecutionStatus.Deferred, "Concurrency conflict during artifact commit");
                }
            }

            stopwatch.Stop();
            _logger.LogInformation("[SceneGenerationCompleted] OutboxId={OutboxId}, JobId={JobId}, SessionId={SessionId}, Revision={Revision}, RequestId={RequestId}, LatencyMs={LatencyMs}",
                outboxId, job.Id, snapshot.SessionId, snapshot.SceneRevision, generationRequestId, stopwatch.ElapsedMilliseconds);

            return new JobExecutionResult(JobExecutionStatus.Completed);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[SceneGenerationInterrupted] JobId={JobId} execution was interrupted by cancellation token. Leaving job in Processing for recovery upon restart.", job.Id);
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
            bool isTransient = DbExceptionClassifier.IsTransient(ex);
            _logger.LogError(ex, "[SceneGenerationError] Exception for JobId={JobId} (IsTransient={IsTransient}): {Message}",
                job.Id, isTransient, ex.Message);
            job.MarkFailed(ex.Message, isRetryable: isTransient, Clock.Now);
            await _dbContext.SaveChangesAsync(CancellationToken.None);
            if (isTransient)
            {
                throw new GpuTransientException($"Transient database/infrastructure error: {ex.Message}", statusCode: null, innerException: ex);
            }
            throw new GpuNonTransientException($"Permanent unclassified error: {ex.Message}", statusCode: null, innerException: ex);
        }
    }
}
