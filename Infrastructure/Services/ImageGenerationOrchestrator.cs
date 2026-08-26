using System.Diagnostics;
using Application.Common;
using Application.DTOs;
using Application.Enums;
using Application.Exceptions;
using Application.Interfaces;
using Application.Services;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Services;

/// <summary>
/// Authoritative orchestrator coordinating the visual generation pipeline.
/// Single Responsibility: Orchestrating discrete lifecycle stages across collaborators (P1-1).
/// </summary>
public sealed class ImageGenerationOrchestrator : IImageGenerationOrchestrator
{
    private readonly ProjectDbContext _dbContext;
    private readonly IVisualPromptCompiler _visualCompiler;
    private readonly IImageGenerationService _imageService;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILogger<ImageGenerationOrchestrator> _logger;
    private readonly IIdentityQualityEvaluator _qualityEvaluator;
    private readonly IdentityQualityGuardPolicy _qualityGuardPolicy;
    private readonly IPredecessorLineageResolver _lineageResolver;
    private readonly IArtifactAcceptanceService _acceptanceService;

    public ImageGenerationOrchestrator(
        ProjectDbContext dbContext,
        IVisualPromptCompiler visualCompiler,
        IImageGenerationService imageService,
        ILogger<ImageGenerationOrchestrator> logger,
        IDateTimeProvider dateTimeProvider,
        IIdentityQualityEvaluator qualityEvaluator,
        IdentityQualityGuardPolicy qualityGuardPolicy,
        IPredecessorLineageResolver lineageResolver,
        IArtifactAcceptanceService acceptanceService)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _visualCompiler = visualCompiler ?? throw new ArgumentNullException(nameof(visualCompiler));
        _imageService = imageService ?? throw new ArgumentNullException(nameof(imageService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dateTimeProvider = dateTimeProvider ?? throw new ArgumentNullException(nameof(dateTimeProvider));
        _qualityEvaluator = qualityEvaluator ?? throw new ArgumentNullException(nameof(qualityEvaluator));
        _qualityGuardPolicy = qualityGuardPolicy ?? throw new ArgumentNullException(nameof(qualityGuardPolicy));
        _lineageResolver = lineageResolver ?? throw new ArgumentNullException(nameof(lineageResolver));
        _acceptanceService = acceptanceService ?? throw new ArgumentNullException(nameof(acceptanceService));
    }

    public async Task<JobExecutionResult> OrchestrateSceneImageGenerationAsync(
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

        // 2. Predecessor Lineage Resolution & Gating (P0-2)
        var (isReady, resolvedPreviousSceneImageUrl, deferReason) = await _lineageResolver.ResolvePredecessorReferenceAsync(
            snapshot.SessionId, snapshot.SceneRevision, snapshot.PredecessorSceneRevision, snapshot.PreviousSceneImageUrl, ct);

        if (!isReady)
        {
            return new JobExecutionResult(JobExecutionStatus.Deferred, deferReason);
        }

        // 3. Lease-based Atomic Job Claim Before GPU Invocation
        var workflow = snapshot.GenerationProfile?.Workflow ?? "VisualIdentity";
        var workflowVersion = snapshot.GenerationProfile?.WorkflowVersion ?? 1;
        var leaseDuration = TimeSpan.FromMinutes(4); // 240s safety lease

        var job = await _dbContext.ImageGenerationJobs
            .FirstOrDefaultAsync(j => j.SessionId == snapshot.SessionId && j.GenerationRequestId == generationRequestId, ct);

        var jobClaimTime = now != default ? now : _dateTimeProvider.UtcNow;
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

            job.TryClaim(workerId, leaseDuration, jobClaimTime);

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

            var claimed = job.TryClaim(workerId, leaseDuration, jobClaimTime);
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
            // 4. Bounded Deterministic Generation & Identity Quality Mitigation Loop
            int maxAttempts = _qualityGuardPolicy.IsActive
                ? _qualityGuardPolicy.MaxAttempts
                : 1;

            int attempt = 1;
            ImageGenerationResult? genResult = null;
            IdentityEvaluationResult? lastEvaluation = null;
            string lastCompiledPrompt = string.Empty;
            string lastAttemptFingerprint = string.Empty;
            bool isIdentityPassed = true;
            long baseSeed = snapshot.GenerationProfile.Seed;
            ImageGenerationAttempt? winningAttemptRecord = null;

            while (attempt <= maxAttempts)
            {
                genResult = null;
                QualityMitigationAction mitigation = attempt == 1
                    ? QualityMitigationAction.Pass
                    : _qualityGuardPolicy.DecideMitigation(attempt - 1, lastEvaluation!);

                var (attemptProfile, derivedSeed) = IdentityMitigationProfileResolver.ResolveMitigation(
                    snapshot, mitigation, attempt, baseSeed);

                var attemptSnapshot = snapshot with
                {
                    GenerationProfile = attemptProfile with { Seed = derivedSeed }
                };

                lastCompiledPrompt = _visualCompiler.CompileScenePrompt(attemptSnapshot);
                var compiledNegative = _visualCompiler.CompileNegativePrompt(attemptSnapshot);

                var attemptFingerprint = DeterministicSeedDerivation.ComputeFingerprint(
                    jobId: job.Id,
                    snapshotTurnId: snapshot.TurnId,
                    sceneRevision: snapshot.SceneRevision,
                    attemptNumber: attempt,
                    derivedSeed: derivedSeed,
                    parametersJson: attemptProfile.ParametersJson ?? string.Empty,
                    workflow: "VisualIdentity",
                    workflowVersion: 1,
                    compiledPrompt: lastCompiledPrompt,
                    compiledNegativePrompt: compiledNegative,
                    previousReferenceUrl: resolvedPreviousSceneImageUrl);
                lastAttemptFingerprint = attemptFingerprint;

                // Check committed artifact by fingerprint
                var existingFingerprintArtifact = await _dbContext.SceneImages
                    .FirstOrDefaultAsync(img => img.GenerationFingerprint == attemptFingerprint, ct);

                if (existingFingerprintArtifact != null)
                {
                    _logger.LogInformation("[SceneGenerationArtifactReused] Existing artifact {ArtifactId} already committed for fingerprint {Fingerprint}. Returning Completed.",
                        existingFingerprintArtifact.Id, attemptFingerprint);

                    if (job.Status != ImageJobStatus.Completed)
                    {
                        var liveTime = _dateTimeProvider.UtcNow;
                        if (_dbContext.Database.IsRelational())
                        {
                            await _dbContext.ImageGenerationJobs
                                .Where(j => j.Id == job.Id && j.Status != ImageJobStatus.Completed)
                                .ExecuteUpdateAsync(s => s
                                    .SetProperty(j => j.Status, ImageJobStatus.Completed)
                                    .SetProperty(j => j.CompletedAt, liveTime)
                                    .SetProperty(j => j.UpdatedAt, liveTime), ct);
                        }
                        else
                        {
                            job.MarkCompleted(liveTime, null);
                            await _dbContext.SaveChangesAsync(ct);
                        }
                    }

                    stopwatch.Stop();
                    return new JobExecutionResult(JobExecutionStatus.Completed);
                }

                var existingAttempt = await _dbContext.ImageGenerationAttempts
                    .FirstOrDefaultAsync(a => a.GenerationFingerprint == attemptFingerprint, ct);

                ImageGenerationAttempt attemptRecord;
                var nowUtc = now;

                if (existingAttempt != null)
                {
                    if (existingAttempt.Status == GenerationAttemptStatus.Succeeded && !string.IsNullOrWhiteSpace(existingAttempt.ImageUrl))
                    {
                        _logger.LogInformation("[SceneGenerationAttemptReused] Reusing existing succeeded attempt {AttemptId} for JobId={JobId}, Attempt={Attempt}, Fingerprint={Fingerprint}",
                            existingAttempt.Id, job.Id, attempt, attemptFingerprint);

                        attemptRecord = existingAttempt;
                        genResult = new ImageGenerationResult(
                            ImageUrl: existingAttempt.ImageUrl,
                            Provider: "ComfyUI",
                            ProviderJobId: existingAttempt.ProviderJobId,
                            DurationMs: 0,
                            Seed: derivedSeed
                        );
                        winningAttemptRecord = existingAttempt;
                        isIdentityPassed = true;
                        break;
                    }
                    else
                    {
                        var attemptClaimTime = now != default ? now : _dateTimeProvider.UtcNow;
                        if (_dbContext.Database.IsRelational())
                        {
                            var rowsAffected = await _dbContext.ImageGenerationAttempts
                                .Where(a => a.Id == existingAttempt.Id
                                            && a.Status != GenerationAttemptStatus.Succeeded
                                            && (a.Status != GenerationAttemptStatus.Running
                                                || a.LeaseUntil == null
                                                || a.LeaseUntil.Value <= attemptClaimTime
                                                || a.ClaimedBy == workerId))
                                .ExecuteUpdateAsync(s => s
                                    .SetProperty(a => a.ClaimedBy, workerId)
                                    .SetProperty(a => a.StartedAt, attemptClaimTime)
                                    .SetProperty(a => a.LeaseUntil, attemptClaimTime.AddMinutes(2))
                                    .SetProperty(a => a.Status, GenerationAttemptStatus.Running), ct);

                            if (rowsAffected != 1)
                            {
                                _logger.LogInformation("[SceneGenerationAttemptClaimFailed] Attempt {AttemptId} is actively claimed by another worker. Deferring.", existingAttempt.Id);
                                return new JobExecutionResult(JobExecutionStatus.Deferred, $"Attempt {attempt} is actively processing by another worker");
                            }

                            existingAttempt.TryClaim(workerId, attemptClaimTime, TimeSpan.FromMinutes(2));
                            attemptRecord = existingAttempt;
                        }
                        else
                        {
                            if (!existingAttempt.TryClaim(workerId, attemptClaimTime, TimeSpan.FromMinutes(2)))
                            {
                                return new JobExecutionResult(JobExecutionStatus.Deferred, $"Attempt {attempt} is actively processing by worker {existingAttempt.ClaimedBy}");
                            }

                            attemptRecord = existingAttempt;
                            try
                            {
                                await _dbContext.SaveChangesAsync(ct);
                            }
                            catch (DbUpdateConcurrencyException)
                            {
                                return new JobExecutionResult(JobExecutionStatus.Deferred, "Attempt lease claimed by concurrent worker");
                            }
                        }
                    }
                }
                else
                {
                    var attemptCreateTime = now != default ? now : _dateTimeProvider.UtcNow;
                    attemptRecord = new ImageGenerationAttempt(
                        generationJobId: job.Id,
                        turnId: snapshot.TurnId,
                        sceneRevision: snapshot.SceneRevision,
                        attemptNumber: attempt,
                        derivedSeed: derivedSeed,
                        parametersJson: attemptProfile.ParametersJson ?? string.Empty,
                        generationFingerprint: attemptFingerprint,
                        status: GenerationAttemptStatus.Running,
                        claimedBy: workerId,
                        startedAt: attemptCreateTime,
                        leaseUntil: attemptCreateTime.AddMinutes(2)
                    );

                    try
                    {
                        await _dbContext.ImageGenerationAttempts.AddAsync(attemptRecord, ct);
                        await _dbContext.SaveChangesAsync(ct);
                    }
                    catch (DbUpdateException ex)
                    {
                        _logger.LogInformation(ex, "[SceneGenerationAttemptRacePrevented] Concurrent worker inserted attempt for Fingerprint={Fingerprint}", attemptFingerprint);
                        return new JobExecutionResult(JobExecutionStatus.Deferred, "Attempt claimed by concurrent worker");
                    }
                }

                winningAttemptRecord = attemptRecord;

                // Emit GenerationAttemptStarted Outbox lifecycle event (PR25 Observability)
                var startedOutbox = new OutboxMessage(
                    eventType: OutboxEventTypes.GenerationAttemptStarted,
                    payloadJson: System.Text.Json.JsonSerializer.Serialize(new Domain.Events.GenerationAttemptStartedEvent(
                        JobId: job.Id,
                        AttemptId: attemptRecord.Id,
                        AttemptNumber: attempt,
                        DerivedSeed: derivedSeed,
                        WorkerId: workerId
                    ))
                );
                await _dbContext.OutboxMessages.AddAsync(startedOutbox, ct);
                await _dbContext.SaveChangesAsync(ct);

                if (genResult == null)
                {
                    var imageReq = ImageGenerationRequest.FromSnapshot(
                        snapshot: attemptSnapshot,
                        compiledPrompt: lastCompiledPrompt,
                        compiledNegative: compiledNegative,
                        previousSceneImageUrlOverride: resolvedPreviousSceneImageUrl,
                        providerJobId: attemptRecord.ProviderJobId ?? job.ProviderJobId,
                        onPromptQueuedAsync: async (promptId, token) =>
                        {
                            job.SetProviderJobId(promptId);
                            attemptRecord.SetProviderJobId(promptId);
                            await _dbContext.SaveChangesAsync(token);
                        }
                    );

                    try
                    {
                        genResult = await _imageService.GenerateImageWithResultAsync(imageReq, ct);
                    }
                    catch (Exception ex)
                    {
                        var failTime = _dateTimeProvider.UtcNow;
                        attemptRecord.MarkFailed(ex.Message, failTime, workerId, failTime);
                        await _dbContext.SaveChangesAsync(ct);
                        throw;
                    }

                    if (string.IsNullOrWhiteSpace(genResult.ImageUrl))
                    {
                        var emptyFailTime = _dateTimeProvider.UtcNow;
                        attemptRecord.MarkFailed("Provider returned empty ImageUrl", emptyFailTime, workerId, emptyFailTime);
                        await _dbContext.SaveChangesAsync(ct);
                        _logger.LogError("[SceneGenerationFailed] Provider returned empty ImageUrl for JobId={JobId}, Attempt={Attempt}.", job.Id, attempt);
                        throw new GpuNonTransientException("Image generation completed without producing an image URL.");
                    }
                }

                if (_qualityGuardPolicy.IsActive)
                {
                    var evalStartTime = _dateTimeProvider.UtcNow;
                    if (attemptRecord.LeaseUntil.HasValue && attemptRecord.LeaseUntil.Value <= evalStartTime)
                    {
                        _logger.LogWarning("[SceneGenerationLeaseExpired] Worker {WorkerId} lease expired at {LeaseUntil:O} (now: {Now:O}) before evaluation. Deferring.",
                            workerId, attemptRecord.LeaseUntil.Value, evalStartTime);
                        return new JobExecutionResult(JobExecutionStatus.Deferred, "Worker lease expired during generation");
                    }

                    attemptRecord.StartEvaluating(workerId, evalStartTime);
                    lastEvaluation = await _qualityEvaluator.EvaluateAsync(genResult.ImageUrl, attemptSnapshot, ct);
                    
                    var evalCompletionTime = _dateTimeProvider.UtcNow;
                    if (attemptRecord.LeaseUntil.HasValue && attemptRecord.LeaseUntil.Value <= evalCompletionTime)
                    {
                        _logger.LogWarning("[SceneGenerationLeaseExpired] Worker {WorkerId} lease expired at {LeaseUntil:O} (now: {Now:O}) during evaluation. Deferring.",
                            workerId, attemptRecord.LeaseUntil.Value, evalCompletionTime);
                        return new JobExecutionResult(JobExecutionStatus.Deferred, "Worker lease expired during evaluation");
                    }

                    if (lastEvaluation.Status == IdentityStatus.Passed)
                    {
                        attemptRecord.MarkSucceeded(genResult.ImageUrl, genResult.ProviderJobId, lastEvaluation.IdentitySimilarity, lastEvaluation.FeatureScore, evalCompletionTime, workerId, evalCompletionTime);
                    }
                    else
                    {
                        // IdentityStatus.Degraded or IdentityStatus.Failed is an evaluation quality outcome on a successfully generated image.
                        // The attempt is marked Degraded (not GenerationAttemptStatus.Failed, which is reserved for GPU/execution crashes).
                        attemptRecord.MarkDegraded(genResult.ImageUrl, genResult.ProviderJobId, lastEvaluation.IdentitySimilarity, lastEvaluation.FeatureScore, evalCompletionTime, workerId, evalCompletionTime);
                    }

                    var evalOutbox = new OutboxMessage(
                        eventType: OutboxEventTypes.GenerationAttemptEvaluated,
                        payloadJson: System.Text.Json.JsonSerializer.Serialize(new Domain.Events.GenerationAttemptEvaluatedEvent(
                            JobId: job.Id,
                            AttemptId: attemptRecord.Id,
                            AttemptNumber: attempt,
                            IdentitySimilarity: lastEvaluation.IdentitySimilarity,
                            FeatureScore: lastEvaluation.FeatureScore,
                            Status: lastEvaluation.Status
                        ))
                    );
                    await _dbContext.OutboxMessages.AddAsync(evalOutbox, ct);
                    await _dbContext.SaveChangesAsync(ct);

                    var nextAction = _qualityGuardPolicy.DecideMitigation(attempt, lastEvaluation);

                    if (nextAction == QualityMitigationAction.Pass)
                    {
                        _logger.LogInformation("[IdentityGuardPassed] JobId={JobId}, Attempt={Attempt}/{MaxAttempts}, IdentitySim={IdentitySim:F4}, Status={Status}",
                            job.Id, attempt, maxAttempts, lastEvaluation.IdentitySimilarity, lastEvaluation.Status);
                        isIdentityPassed = true;
                        break;
                    }
                    else if (nextAction == QualityMitigationAction.RejectDegraded)
                    {
                        _logger.LogWarning("[IdentityGuardExhausted] JobId={JobId}, Max attempts {MaxAttempts} reached. Quarantining frame.", job.Id, maxAttempts);
                        isIdentityPassed = false;
                        break;
                    }
                    else
                    {
                        _logger.LogWarning("[IdentityGuardDegraded] JobId={JobId}, Attempt={Attempt}/{MaxAttempts} degraded (IdentitySim={IdentitySim:F4}). Escalating to {NextAction}.",
                            job.Id, attempt, maxAttempts, lastEvaluation.IdentitySimilarity, nextAction);
                        attempt++;
                    }
                }
                else
                {
                    var compTime = _dateTimeProvider.UtcNow;
                    if (attemptRecord.LeaseUntil.HasValue && attemptRecord.LeaseUntil.Value <= compTime)
                    {
                        _logger.LogWarning("[SceneGenerationLeaseExpired] Worker {WorkerId} lease expired at {LeaseUntil:O} (now: {Now:O}) before completion. Deferring.",
                            workerId, attemptRecord.LeaseUntil.Value, compTime);
                        return new JobExecutionResult(JobExecutionStatus.Deferred, "Worker lease expired during generation");
                    }

                    attemptRecord.MarkSucceeded(genResult.ImageUrl, genResult.ProviderJobId, null, null, compTime, workerId, compTime);
                    await _dbContext.SaveChangesAsync(ct);
                    isIdentityPassed = true;
                    break;
                }
            }

            // 5. Atomic Acceptance Fencing & Artifact Persistence (P0-1)
            var acceptanceRequest = new ArtifactAcceptanceRequest(
                Job: job,
                WinningAttempt: winningAttemptRecord!,
                Snapshot: snapshot,
                ImageUrl: genResult!.ImageUrl,
                CompiledPrompt: lastCompiledPrompt,
                ResolvedPreviousSceneImageUrl: resolvedPreviousSceneImageUrl,
                GenerationFingerprint: lastAttemptFingerprint,
                MetadataJson: genResult.MetadataJson,
                IsIdentityPassed: isIdentityPassed,
                WorkerId: workerId,
                OutboxId: outboxId
            );

            var acceptanceResult = await _acceptanceService.AcceptAttemptAtomicallyAsync(acceptanceRequest, ct);

            stopwatch.Stop();
            _logger.LogInformation("[SceneGenerationJobFinished] OutboxId={OutboxId}, JobId={JobId}, Revision={Revision}, Attempts={Attempts}/{MaxAttempts}, DurationMs={DurationMs}, Status={Status}",
                outboxId, job.Id, snapshot.SceneRevision, attempt, maxAttempts, stopwatch.ElapsedMilliseconds, acceptanceResult.Status);

            return acceptanceResult;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[SceneGenerationCancelled] Execution cancelled for JobId={JobId}. Lease left to expire for recovery.", job.Id);
            throw;
        }
        catch (GpuNonTransientException ex)
        {
            _logger.LogError(ex, "[SceneGenerationFatalError] Non-transient failure for JobId={JobId}, OutboxId={OutboxId}: {Message}", job.Id, outboxId, ex.Message);
            var failTime = _dateTimeProvider.UtcNow;
            try
            {
                if (_dbContext.Database.IsRelational())
                {
                    await _dbContext.ImageGenerationJobs
                        .Where(j => j.Id == job.Id && j.ClaimedBy == workerId && j.LeaseUntil.HasValue && j.LeaseUntil.Value > failTime)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(j => j.Status, ImageJobStatus.Failed)
                            .SetProperty(j => j.FailureReason, ex.Message)
                            .SetProperty(j => j.IsRetryable, false)
                            .SetProperty(j => j.CompletedAt, failTime)
                            .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                            .SetProperty(j => j.Version, j => j.Version + 1)
                            .SetProperty(j => j.UpdatedAt, failTime), CancellationToken.None);
                }
                else
                {
                    job.Fail(ex.Message, isRetryable: false, now: failTime, workerId: workerId);
                    await _dbContext.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception failEx)
            {
                _logger.LogWarning(failEx, "[SceneGenerationFailIgnored] Stale worker {WorkerId} cannot mark JobId={JobId} failed because lease expired or was lost.", workerId, job.Id);
            }
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SceneGenerationTransientError] Transient failure for JobId={JobId}, OutboxId={OutboxId}: {Message}. Releasing lease for retry.", job.Id, outboxId, ex.Message);
            var failTime = _dateTimeProvider.UtcNow;
            try
            {
                if (_dbContext.Database.IsRelational())
                {
                    await _dbContext.ImageGenerationJobs
                        .Where(j => j.Id == job.Id && j.ClaimedBy == workerId && j.LeaseUntil.HasValue && j.LeaseUntil.Value > failTime)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(j => j.Status, ImageJobStatus.Failed)
                            .SetProperty(j => j.FailureReason, ex.Message)
                            .SetProperty(j => j.IsRetryable, true)
                            .SetProperty(j => j.CompletedAt, failTime)
                            .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                            .SetProperty(j => j.Version, j => j.Version + 1)
                            .SetProperty(j => j.UpdatedAt, failTime), CancellationToken.None);
                }
                else
                {
                    job.Fail(ex.Message, isRetryable: true, now: failTime, workerId: workerId);
                    await _dbContext.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception failEx)
            {
                _logger.LogWarning(failEx, "[SceneGenerationFailIgnored] Stale worker {WorkerId} cannot mark JobId={JobId} failed because lease expired or was lost.", workerId, job.Id);
            }
            throw;
        }
    }
}
