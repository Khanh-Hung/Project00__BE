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

namespace Infrastructure.Services;

public sealed class ImageGenerationJobHandler : IImageGenerationJobHandler
{
    private readonly ProjectDbContext _dbContext;
    private readonly IVisualPromptCompiler _visualCompiler;
    private readonly IImageGenerationService _imageService;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILogger<ImageGenerationJobHandler> _logger;
    private readonly IIdentityQualityEvaluator _qualityEvaluator;
    private readonly IdentityQualityGuardPolicy _qualityGuardPolicy;

    public ImageGenerationJobHandler(
        ProjectDbContext dbContext,
        IVisualPromptCompiler visualCompiler,
        IImageGenerationService imageService,
        ILogger<ImageGenerationJobHandler> logger,
        IDateTimeProvider dateTimeProvider,
        IIdentityQualityEvaluator qualityEvaluator,
        IdentityQualityGuardPolicy? qualityGuardPolicy = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _visualCompiler = visualCompiler ?? throw new ArgumentNullException(nameof(visualCompiler));
        _imageService = imageService ?? throw new ArgumentNullException(nameof(imageService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dateTimeProvider = dateTimeProvider ?? throw new ArgumentNullException(nameof(dateTimeProvider));
        _qualityEvaluator = qualityEvaluator ?? throw new ArgumentNullException(nameof(qualityEvaluator));
        _qualityGuardPolicy = qualityGuardPolicy ?? IdentityQualityGuardPolicy.Default;
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
                    .Where(m => m.EventType == OutboxEventTypes.SceneImageGeneration)
                    .ToListAsync(ct);

                var predMatchingMsg = predecessorMsg.FirstOrDefault(m =>
                {
                    try
                    {
                        var p = System.Text.Json.JsonSerializer.Deserialize<SceneImageGenerationOutboxPayload>(m.PayloadJson);
                        return p?.Snapshot?.SessionId == snapshot.SessionId && p?.Snapshot?.SceneRevision == predRev;
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

            while (attempt <= maxAttempts)
            {
                genResult = null;
                QualityMitigationAction mitigation = attempt == 1
                    ? QualityMitigationAction.Pass
                    : _qualityGuardPolicy.DecideMitigation(attempt - 1, lastEvaluation!);

                var (attemptProfile, derivedSeed) = IdentityMitigationProfileResolver.ResolveMitigation(
                    snapshot, mitigation, attempt, baseSeed);

                // Derive attempt snapshot with adjusted profile and deterministic seed
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

                // Database-enforced Idempotency & Crash-Safety: Check existing artifact or durable attempt ledger
                var existingFingerprintArtifact = await _dbContext.SceneImages
                    .FirstOrDefaultAsync(img => img.GenerationFingerprint == attemptFingerprint, ct);

                if (existingFingerprintArtifact != null)
                {
                    _logger.LogInformation("[SceneGenerationArtifactReused] Existing artifact {ArtifactId} already committed for fingerprint {Fingerprint}. Skipping generation & duplicate insertion.",
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
                    }
                    else
                    {
                        // Database-atomic claim of existing/stale attempt row
                        if (_dbContext.Database.IsRelational())
                        {
                            var rowsAffected = await _dbContext.ImageGenerationAttempts
                                .Where(a => a.Id == existingAttempt.Id
                                            && a.Status != GenerationAttemptStatus.Succeeded
                                            && (a.Status != GenerationAttemptStatus.Running
                                                || a.LeaseUntil == null
                                                || a.LeaseUntil.Value <= nowUtc
                                                || a.ClaimedBy == workerId))
                                .ExecuteUpdateAsync(s => s
                                    .SetProperty(a => a.ClaimedBy, workerId)
                                    .SetProperty(a => a.StartedAt, nowUtc)
                                    .SetProperty(a => a.LeaseUntil, nowUtc.AddMinutes(2))
                                    .SetProperty(a => a.Status, GenerationAttemptStatus.Running), ct);

                            if (rowsAffected != 1)
                            {
                                _logger.LogInformation("[SceneGenerationAttemptClaimFailed] Attempt {AttemptId} is actively claimed by another worker. Deferring execution.", existingAttempt.Id);
                                return new JobExecutionResult(JobExecutionStatus.Deferred, $"Attempt {attempt} is actively processing by another worker");
                            }

                            existingAttempt.TryClaim(workerId, nowUtc, TimeSpan.FromMinutes(2));
                            attemptRecord = existingAttempt;
                        }
                        else
                        {
                            if (!existingAttempt.TryClaim(workerId, nowUtc, TimeSpan.FromMinutes(2)))
                            {
                                _logger.LogInformation("[SceneGenerationAttemptActive] Attempt {AttemptId} is actively running by worker '{ClaimedBy}' (LeaseUntil={LeaseUntil}). Deferring execution.",
                                    existingAttempt.Id, existingAttempt.ClaimedBy, existingAttempt.LeaseUntil);
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
                        startedAt: nowUtc,
                        leaseUntil: nowUtc.AddMinutes(2)
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
                        attemptRecord.MarkFailed(ex.Message, _dateTimeProvider.UtcNow);
                        await _dbContext.SaveChangesAsync(ct);
                        throw;
                    }

                    if (string.IsNullOrWhiteSpace(genResult.ImageUrl))
                    {
                        attemptRecord.MarkFailed("Provider returned empty ImageUrl", _dateTimeProvider.UtcNow);
                        await _dbContext.SaveChangesAsync(ct);
                        _logger.LogError("[SceneGenerationFailed] Provider returned empty ImageUrl for JobId={JobId}, Attempt={Attempt}.", job.Id, attempt);
                        throw new GpuNonTransientException("Image generation completed without producing an image URL.");
                    }
                }

                if (_qualityGuardPolicy.IsActive)
                {
                    lastEvaluation = await _qualityEvaluator.EvaluateAsync(genResult.ImageUrl, attemptSnapshot, ct);
                    
                    if (lastEvaluation.Status == IdentityStatus.Passed)
                    {
                        attemptRecord.MarkSucceeded(genResult.ImageUrl, genResult.ProviderJobId, lastEvaluation.IdentitySimilarity, lastEvaluation.FeatureScore, _dateTimeProvider.UtcNow);
                    }
                    else if (lastEvaluation.Status == IdentityStatus.Degraded)
                    {
                        attemptRecord.MarkDegraded(genResult.ImageUrl, genResult.ProviderJobId, lastEvaluation.IdentitySimilarity, lastEvaluation.FeatureScore, _dateTimeProvider.UtcNow);
                    }
                    else
                    {
                        var violationMsg = lastEvaluation.Violations.Count > 0
                            ? string.Join("; ", lastEvaluation.Violations.Select(v => v.Code))
                            : "Hard invariant violation";
                        attemptRecord.MarkFailed($"Identity invariant violated: {violationMsg}", _dateTimeProvider.UtcNow);
                    }
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
                        _logger.LogWarning("[IdentityGuardExhausted] JobId={JobId}, Max attempts {MaxAttempts} reached without passing. Quarantining frame from continuity.",
                            job.Id, maxAttempts);
                        isIdentityPassed = false;
                        break;
                    }
                    else
                    {
                        _logger.LogWarning("[IdentityGuardDegraded] JobId={JobId}, Attempt={Attempt}/{MaxAttempts} degraded (IdentitySim={IdentitySim:F4}, Status={Status}). Triggering {NextAction}.",
                            job.Id, attempt, maxAttempts, lastEvaluation.IdentitySimilarity, lastEvaluation.Status, nextAction);
                        attempt++;
                    }
                }
                else
                {
                    attemptRecord.MarkSucceeded(genResult.ImageUrl, genResult.ProviderJobId, null, null, _dateTimeProvider.UtcNow);
                    await _dbContext.SaveChangesAsync(ct);
                    isIdentityPassed = true;
                    break;
                }
            }

            // 5. Strict Atomic DB Conditional Fencing & Artifact Persistence in ONE Unit of Work
            var liveUtc = _dateTimeProvider.UtcNow;

            if (_dbContext.Database.IsRelational())
            {
                try
                {
                    await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);

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
                            .SetProperty(j => j.GenerationMetadataJson, genResult!.MetadataJson)
                            .SetProperty(j => j.Version, j => j.Version + 1)
                            .SetProperty(j => j.UpdatedAt, liveUtc), ct);

                    if (rowsAffected != 1)
                    {
                        _logger.LogWarning("[SceneGenerationStaleDiscarded] Atomic conditional update fencing failed for JobId={JobId}, WorkerId={WorkerId}. Rows affected: {Rows}. Discarding artifact.",
                            job.Id, workerId, rowsAffected);
                        await transaction.RollbackAsync(ct);
                        return new JobExecutionResult(JobExecutionStatus.Deferred, "Worker lease lost or expired before conditional update");
                    }

                    // P0 Hard Gate: If identity passed, promote to isCurrent=true and demote previous currents.
                    // If degraded/quarantined, save with isCurrent=false, keeping the last-known-good reference intact!
                    if (isIdentityPassed)
                    {
                        await _dbContext.SceneImages
                            .Where(img => img.SessionId == snapshot.SessionId && img.SceneRevision == snapshot.SceneRevision && img.IsCurrent)
                            .ExecuteUpdateAsync(s => s.SetProperty(img => img.IsCurrent, false), ct);
                    }

                    var artifact = new SceneImage(
                        sessionId: snapshot.SessionId,
                        characterId: snapshot.CharacterId,
                        turnId: snapshot.TurnId,
                        sceneRevision: snapshot.SceneRevision,
                        imageUrl: genResult!.ImageUrl,
                        prompt: lastCompiledPrompt,
                        generationRequestId: generationRequestId,
                        generationJobId: job.Id,
                        identityReferenceUrl: snapshot.IdentityReferenceUrl,
                        previousSceneImageUrl: resolvedPreviousSceneImageUrl,
                        workflow: workflow,
                        workflowVersion: workflowVersion,
                        isCurrent: isIdentityPassed,
                        generationFingerprint: lastAttemptFingerprint
                    );

                    await _dbContext.SceneImages.AddAsync(artifact, ct);
                    await _dbContext.SaveChangesAsync(ct);

                    await transaction.CommitAsync(ct);
                }
                catch (DbUpdateException ex)
                {
                    bool isUnique = ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
                                 || ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);
                    if (isUnique)
                    {
                        _logger.LogInformation("[SceneGenerationDuplicateArtifactPrevented] Concurrent worker already committed artifact for JobId={JobId}, Fingerprint={Fingerprint}. Returning Completed.",
                            job.Id, lastAttemptFingerprint);
                        return new JobExecutionResult(JobExecutionStatus.Completed, "Artifact committed by concurrent worker");
                    }

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

                if (isIdentityPassed)
                {
                    var previousCurrents = await _dbContext.SceneImages
                        .Where(img => img.SessionId == snapshot.SessionId && img.SceneRevision == snapshot.SceneRevision && img.IsCurrent)
                        .ToListAsync(ct);

                    foreach (var prev in previousCurrents)
                    {
                        prev.SetCurrent(false);
                    }
                }

                var artifact = new SceneImage(
                    sessionId: snapshot.SessionId,
                    characterId: snapshot.CharacterId,
                    turnId: snapshot.TurnId,
                    sceneRevision: snapshot.SceneRevision,
                    imageUrl: genResult!.ImageUrl,
                    prompt: lastCompiledPrompt,
                    generationRequestId: generationRequestId,
                    generationJobId: job.Id,
                    identityReferenceUrl: snapshot.IdentityReferenceUrl,
                    previousSceneImageUrl: resolvedPreviousSceneImageUrl,
                    workflow: workflow,
                    workflowVersion: workflowVersion,
                    isCurrent: isIdentityPassed,
                    generationFingerprint: lastAttemptFingerprint
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
            _logger.LogInformation("[SceneGenerationCompleted] OutboxId={OutboxId}, JobId={JobId}, SessionId={SessionId}, Revision={Revision}, RequestId={RequestId}, LatencyMs={LatencyMs}, IdentityPassed={Passed}",
                outboxId, job.Id, snapshot.SessionId, snapshot.SceneRevision, generationRequestId, stopwatch.ElapsedMilliseconds, isIdentityPassed);

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
