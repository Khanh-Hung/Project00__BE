using System.Diagnostics;
using System.Text.Json;
using Application.Common;
using Application.DTOs;
using Application.Enums;
using Application.Exceptions;
using Application.Interfaces;
using Application.Services;
using Application.Telemetry;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Services;

/// <summary>
/// Authoritative orchestrator coordinating the visual generation pipeline.
/// Single Responsibility: Orchestrating discrete lifecycle stages across collaborators (P1-1).
/// Production-Observable: Collects stage timings, enforces retry budget, attaches provenance, and emits metrics.
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
    private readonly IGenerationMetrics _metrics;
    private readonly IGenerationFingerprintService _fingerprintService;
    private readonly GenerationRetryBudget _retryBudget;

    public ImageGenerationOrchestrator(
        ProjectDbContext dbContext,
        IVisualPromptCompiler visualCompiler,
        IImageGenerationService imageService,
        ILogger<ImageGenerationOrchestrator> logger,
        IDateTimeProvider dateTimeProvider,
        IIdentityQualityEvaluator qualityEvaluator,
        IdentityQualityGuardPolicy qualityGuardPolicy,
        IPredecessorLineageResolver lineageResolver,
        IArtifactAcceptanceService acceptanceService,
        IGenerationMetrics? metrics = null,
        IGenerationFingerprintService? fingerprintService = null,
        GenerationRetryBudget? retryBudget = null)
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
        _metrics = metrics ?? new Infrastructure.Telemetry.GenerationMetrics(NullLogger<Infrastructure.Telemetry.GenerationMetrics>.Instance);
        _fingerprintService = fingerprintService ?? new GenerationFingerprintService();
        _retryBudget = retryBudget ?? GenerationRetryBudget.Default;
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

        var totalStopwatch = Stopwatch.StartNew();
        TimeSpan cumulativeGenLatency = TimeSpan.Zero;
        TimeSpan cumulativeEvalLatency = TimeSpan.Zero;
        TimeSpan acceptanceLatency = TimeSpan.Zero;

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
                userId: payload.UserId,
                outboxMessageId: outboxId,
                provider: "ComfyUI",
                workflow: workflow,
                workflowVersion: workflowVersion,
                generationMetadataJson: JsonSerializer.Serialize(payload)
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
                if (_dbContext.Database.IsRelational())
                {
                    var expectedVersion = job.Version;
                    var rowsClaimed = await _dbContext.ImageGenerationJobs
                        .Where(j => j.Id == job.Id
                                    && j.Version == expectedVersion
                                    && (j.Status == ImageJobStatus.Pending
                                        || j.Status == ImageJobStatus.Queued
                                        || ((j.Status == ImageJobStatus.Processing || j.Status == ImageJobStatus.Evaluating)
                                            && (!j.LeaseUntil.HasValue || j.LeaseUntil.Value <= jobClaimTime))))
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(j => j.ClaimedBy, workerId)
                            .SetProperty(j => j.LeaseUntil, jobClaimTime.Add(leaseDuration))
                            .SetProperty(j => j.StartedAt, jobClaimTime)
                            .SetProperty(j => j.Status, ImageJobStatus.Processing)
                            .SetProperty(j => j.Version, j => j.Version + 1)
                            .SetProperty(j => j.UpdatedAt, jobClaimTime), ct);

                    if (rowsClaimed == 0)
                    {
                        _logger.LogInformation("[SceneGenerationLeaseContended] JobId={JobId} claimed by concurrent worker. Deferring.", job.Id);
                        return new JobExecutionResult(JobExecutionStatus.Deferred, "Job under active lease by another worker");
                    }
                }
                else
                {
                    _logger.LogInformation("[SceneGenerationLeaseContended] JobId={JobId} claimed by concurrent worker {ClaimedBy} until {LeaseUntil:O}.",
                        job.Id, job.ClaimedBy, job.LeaseUntil);
                    return new JobExecutionResult(JobExecutionStatus.Deferred, "Job under active lease by another worker");
                }
            }
            else
            {
                try
                {
                    await _dbContext.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException)
                {
                    _logger.LogInformation("[SceneGenerationClaimRace] Concurrency conflict claiming JobId={JobId}. Deferring.", job.Id);
                    return new JobExecutionResult(JobExecutionStatus.Deferred, "Job claim race conflict");
                }
            }
        }

        _metrics.RecordGenerationStarted(job.Id, generationRequestId);

        // 4. Quality Evaluation & Progressive Mitigation Loop
        int maxAttempts = _qualityGuardPolicy.IsActive ? _qualityGuardPolicy.MaxAttempts : 1;
        int attempt = 1;
        ImageGenerationResult? genResult = null;
        ImageGenerationResult? lastSuccessfulGenResult = null;
        IdentityEvaluationResult? lastEvaluation = null;
        ImageGenerationAttempt? winningAttemptRecord = null;
        QualityMitigationAction winningMitigationAction = QualityMitigationAction.Pass;
        GenerationProfile? winningProfile = snapshot.GenerationProfile;
        string? lastCompiledPrompt = null;
        string? lastAttemptFingerprint = null;
        bool isIdentityPassed = false;

        try
        {
            while (attempt <= maxAttempts)
            {
                genResult = null;
                _metrics.RecordAttemptStarted(job.Id, attempt);

                // Enforce cost and time-bounded retry budget before starting attempt > 1
                if (attempt > 1)
                {
                    if (!_retryBudget.CanRetryMitigation(attempt, totalStopwatch.Elapsed, out var budgetReason))
                    {
                        _logger.LogWarning("[RetryBudgetExhausted] JobId={JobId}, Attempt={Attempt}: {Reason}", job.Id, attempt, budgetReason);
                        isIdentityPassed = false;
                        break;
                    }
                }

                using var attemptCts = _retryBudget.CreateAttemptCancellationTokenSource(ct, totalStopwatch.Elapsed);
                var attemptCt = attemptCts.Token;

                QualityMitigationAction currentAction = (attempt == 1) ? QualityMitigationAction.Pass : _qualityGuardPolicy.DecideMitigation(attempt - 1, lastEvaluation!);
                var baseSeed = snapshot.GenerationProfile?.Seed ?? 12345;
                var (attemptProfile, derivedSeed) = IdentityMitigationProfileResolver.ResolveMitigation(snapshot, currentAction, attempt, baseSeed);
                var attemptSnapshot = snapshot with { GenerationProfile = attemptProfile };
                winningMitigationAction = currentAction;
                winningProfile = attemptProfile;

                lastCompiledPrompt = _visualCompiler.CompileScenePrompt(attemptSnapshot);
                var compiledNegative = _visualCompiler.CompileNegativePrompt(attemptSnapshot);

                var attemptFingerprint = _fingerprintService.ComputeFingerprint(
                    jobId: job.Id,
                    snapshot: attemptSnapshot,
                    profile: attemptProfile,
                    derivedSeed: derivedSeed,
                    attemptNumber: attempt,
                    workflow: workflow,
                    workflowVersion: workflowVersion,
                    modelIdentifier: snapshot.GenerationProfile?.Model,
                    compiledPrompt: lastCompiledPrompt,
                    compiledNegativePrompt: compiledNegative,
                    previousReferenceUrl: resolvedPreviousSceneImageUrl,
                    mitigationAction: currentAction.ToString()
                );
                lastAttemptFingerprint = attemptFingerprint;

                // Check committed artifact by fingerprint
                var existingFingerprintArtifact = await _dbContext.SceneImages
                    .FirstOrDefaultAsync(img => img.GenerationFingerprint == attemptFingerprint, ct);

                if (existingFingerprintArtifact != null)
                {
                    _logger.LogInformation("[SceneGenerationArtifactReused] Existing artifact {ArtifactId} already committed for fingerprint {Fingerprint}. Resolving attempt and routing through authoritative acceptance.",
                        existingFingerprintArtifact.Id, attemptFingerprint);

                    var existingAttemptForArtifact = await _dbContext.ImageGenerationAttempts
                        .FirstOrDefaultAsync(a => a.GenerationFingerprint == attemptFingerprint, ct);

                    var liveTime = _dateTimeProvider.UtcNow;
                    if (existingAttemptForArtifact == null)
                    {
                        existingAttemptForArtifact = new ImageGenerationAttempt(
                            generationJobId: job.Id,
                            turnId: snapshot.TurnId,
                            sceneRevision: snapshot.SceneRevision,
                            attemptNumber: attempt,
                            derivedSeed: derivedSeed,
                            parametersJson: attemptProfile.ParametersJson ?? string.Empty,
                            generationFingerprint: attemptFingerprint,
                            status: GenerationAttemptStatus.Running,
                            claimedBy: workerId,
                            startedAt: liveTime,
                            leaseUntil: liveTime.AddMinutes(2)
                        );
                        existingAttemptForArtifact.MarkSucceeded(existingFingerprintArtifact.ImageUrl, existingFingerprintArtifact.Workflow, null, null, liveTime, workerId, liveTime);
                        await _dbContext.ImageGenerationAttempts.AddAsync(existingAttemptForArtifact, ct);
                        await _dbContext.SaveChangesAsync(ct);
                    }
                    else if (existingAttemptForArtifact.Status != GenerationAttemptStatus.Succeeded)
                    {
                        existingAttemptForArtifact.MarkSucceeded(existingFingerprintArtifact.ImageUrl, existingFingerprintArtifact.Workflow, null, null, liveTime, workerId, liveTime);
                        await _dbContext.SaveChangesAsync(ct);
                    }

                    winningAttemptRecord = existingAttemptForArtifact;
                    genResult = new ImageGenerationResult(
                        ImageUrl: existingFingerprintArtifact.ImageUrl,
                        Provider: existingFingerprintArtifact.Workflow,
                        ProviderJobId: null,
                        DurationMs: 0,
                        Seed: derivedSeed
                    );
                    lastSuccessfulGenResult = genResult;
                    isIdentityPassed = true;
                    break;
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
                                            && (!a.LeaseUntil.HasValue || a.LeaseUntil.Value <= attemptClaimTime))
                                .ExecuteUpdateAsync(s => s
                                    .SetProperty(a => a.ClaimedBy, workerId)
                                    .SetProperty(a => a.StartedAt, attemptClaimTime)
                                    .SetProperty(a => a.LeaseUntil, attemptClaimTime.AddMinutes(2))
                                    .SetProperty(a => a.Status, GenerationAttemptStatus.Running)
                                    .SetProperty(a => a.UpdatedAt, attemptClaimTime), ct);

                            if (rowsAffected == 0)
                            {
                                _logger.LogInformation("[SceneGenerationAttemptContended] Attempt {AttemptId} claimed by concurrent worker. Deferring.", existingAttempt.Id);
                                return new JobExecutionResult(JobExecutionStatus.Deferred, "Attempt under active lease by another worker");
                            }

                            await _dbContext.Entry(existingAttempt).ReloadAsync(ct);
                        }
                        else
                        {
                            var claimed = existingAttempt.TryClaim(workerId, attemptClaimTime, TimeSpan.FromMinutes(2));
                            if (!claimed)
                            {
                                _logger.LogInformation("[SceneGenerationAttemptContended] Attempt {AttemptId} claimed by concurrent worker. Deferring.", existingAttempt.Id);
                                return new JobExecutionResult(JobExecutionStatus.Deferred, "Attempt under active lease by another worker");
                            }
                            await _dbContext.SaveChangesAsync(ct);
                        }

                        attemptRecord = existingAttempt;
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

                // Emit GenerationAttemptStarted Outbox lifecycle event
                var startedOutbox = new OutboxMessage(
                    eventType: OutboxEventTypes.GenerationAttemptStarted,
                    payloadJson: JsonSerializer.Serialize(new Domain.Events.GenerationAttemptStartedEvent(
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
                        var genSw = Stopwatch.StartNew();
                        genResult = await _imageService.GenerateImageWithResultAsync(imageReq, attemptCt);
                        genSw.Stop();
                        cumulativeGenLatency += genSw.Elapsed;
                        lastSuccessfulGenResult = genResult;
                    }
                    catch (Exception ex)
                    {
                        var failTime = _dateTimeProvider.UtcNow;
                        var category = GenerationFailureClassifier.Classify(ex);
                        var currentAttempt = Math.Max(attempt, job.AttemptCount);
                        var isRetryable = _retryBudget.CanRetryFailure(currentAttempt, totalStopwatch.Elapsed, category, out var failBudgetReason);

                        attemptRecord.MarkFailed(category, ex.Message, failTime, workerId, failTime);
                        await _dbContext.SaveChangesAsync(ct);

                        if (!isRetryable)
                        {
                            _logger.LogError(ex, "[GenerationFailureTerminal] JobId={JobId}, Attempt={Attempt} failed terminally ({Category}: {Message}). Reason={Reason}",
                                job.Id, attempt, category, ex.Message, failBudgetReason ?? "Non-retryable");
                            throw new GpuNonTransientException($"Terminal generation failure: {ex.Message} ({failBudgetReason})", innerException: ex);
                        }

                        var retryDelay = GenerationRetryPolicy.Default.CalculateDelay(job.RetryCount);
                        _logger.LogWarning("[GenerationFailureRetryable] JobId={JobId}, Attempt={Attempt} failed ({Category}: {Message}). Scheduling worker backoff retry (Delay: {DelayMs:F0}ms).",
                            job.Id, attempt, category, ex.Message, retryDelay.TotalMilliseconds);
                        _metrics.RecordGenerationRetry(job.Id, attempt, retryDelay);
                        throw;
                    }

                    if (string.IsNullOrWhiteSpace(genResult.ImageUrl))
                    {
                        var emptyFailTime = _dateTimeProvider.UtcNow;
                        attemptRecord.MarkFailed(GenerationFailureCategory.InvalidWorkflow, "Provider returned empty ImageUrl", emptyFailTime, workerId, emptyFailTime);
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

                    var evalSw = Stopwatch.StartNew();
                    lastEvaluation = await _qualityEvaluator.EvaluateAsync(genResult.ImageUrl, attemptSnapshot, attemptCt);
                    evalSw.Stop();
                    cumulativeEvalLatency += evalSw.Elapsed;

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
                        attemptRecord.MarkDegraded(genResult.ImageUrl, genResult.ProviderJobId, lastEvaluation.IdentitySimilarity, lastEvaluation.FeatureScore, evalCompletionTime, workerId, evalCompletionTime);
                    }

                    var nextAction = _qualityGuardPolicy.DecideMitigation(attempt, lastEvaluation);
                    bool isPassed = (nextAction == QualityMitigationAction.Pass) || (lastEvaluation.Status == IdentityStatus.Passed);
                    bool willRetry = !isPassed && (nextAction != QualityMitigationAction.RejectDegraded) && (attempt < maxAttempts) && _retryBudget.CanRetryMitigation(attempt + 1, totalStopwatch.Elapsed, out _);

                    _metrics.RecordIdentityEvaluation(
                        job.Id,
                        attemptRecord.Id,
                        attempt,
                        lastEvaluation.IdentitySimilarity,
                        lastEvaluation.FeatureScore,
                        isPassed,
                        willRetry,
                        evalSw.Elapsed);

                    var evalOutbox = new OutboxMessage(
                        eventType: OutboxEventTypes.GenerationAttemptEvaluated,
                        payloadJson: JsonSerializer.Serialize(new Domain.Events.GenerationAttemptEvaluatedEvent(
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
                        _metrics.RecordGenerationRetry(job.Id, attempt, TimeSpan.Zero);
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

            // 5. Formulate Provenance Record from Actual Winning Attempt Profile
            var nowTime = _dateTimeProvider.UtcNow;
            float actualSlot1Weight = 1.0f;
            float actualSlot2Weight = 0.0f;
            string actualSlot2Mode = "Disabled";

            if (!string.IsNullOrWhiteSpace(winningProfile?.ParametersJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(winningProfile.ParametersJson);
                    if (doc.RootElement.TryGetProperty("ipAdapter", out var ipProp) && ipProp.TryGetProperty("weight", out var w1))
                    {
                        actualSlot1Weight = (float)w1.GetDouble();
                    }
                    if (doc.RootElement.TryGetProperty("sceneContinuity", out var contProp) && contProp.TryGetProperty("weight", out var w2))
                    {
                        actualSlot2Weight = (float)w2.GetDouble();
                        actualSlot2Mode = actualSlot2Weight > 0f ? "SceneStyleContinuity" : "Disabled";
                    }
                }
                catch
                {
                    // Fallback to default continuous weights if parameters parsing fails
                }
            }
            else if (snapshot.VisualIdentity != null && resolvedPreviousSceneImageUrl != null)
            {
                actualSlot1Weight = 0.60f;
                actualSlot2Weight = 0.12f;
                actualSlot2Mode = "SceneStyleContinuity";
            }

            var provenance = new GenerationProvenance(
                generationRequestId: generationRequestId,
                jobId: job.Id,
                attemptId: winningAttemptRecord!.Id,
                sceneRevision: snapshot.SceneRevision,
                derivedSeed: winningAttemptRecord.DerivedSeed,
                generationFingerprint: lastAttemptFingerprint ?? string.Empty,
                workflow: workflow,
                workflowVersion: workflowVersion,
                modelIdentifier: snapshot.GenerationProfile?.Model ?? "ComfyUI/SDXL",
                slot1Weight: actualSlot1Weight,
                slot2Weight: actualSlot2Weight,
                slot2ConditioningMode: actualSlot2Mode,
                mitigationAction: winningMitigationAction.ToString(),
                identitySimilarity: winningAttemptRecord.IdentitySimilarity,
                featureScore: winningAttemptRecord.FeatureScore,
                identityStatus: isIdentityPassed ? "Passed" : "Quarantined",
                createdAt: nowTime
            );

            var finalImageUrl = genResult?.ImageUrl ?? lastSuccessfulGenResult?.ImageUrl ?? winningAttemptRecord.ImageUrl ?? string.Empty;
            var finalMetadataJson = genResult?.MetadataJson ?? lastSuccessfulGenResult?.MetadataJson ?? winningAttemptRecord.ParametersJson;

            // 6. Atomic Acceptance Fencing & Artifact Persistence (P0-1)
            var acceptanceRequest = new ArtifactAcceptanceRequest(
                JobId: job.Id,
                WinningAttemptId: winningAttemptRecord.Id,
                Snapshot: snapshot,
                ImageUrl: finalImageUrl,
                CompiledPrompt: lastCompiledPrompt ?? string.Empty,
                ResolvedPreviousSceneImageUrl: resolvedPreviousSceneImageUrl,
                GenerationFingerprint: lastAttemptFingerprint ?? string.Empty,
                MetadataJson: finalMetadataJson,
                IsIdentityPassed: isIdentityPassed,
                WorkerId: workerId,
                OutboxId: outboxId,
                Provenance: provenance
            );

            var acceptSw = Stopwatch.StartNew();
            var acceptanceResult = await _acceptanceService.AcceptAttemptAtomicallyAsync(acceptanceRequest, ct);
            acceptSw.Stop();
            acceptanceLatency = acceptSw.Elapsed;

            totalStopwatch.Stop();
            var queueLatency = jobClaimTime > job.CreatedAt ? jobClaimTime - job.CreatedAt : TimeSpan.Zero;
            var timing = new GenerationTiming(
                QueueLatency: queueLatency,
                GenerationLatency: cumulativeGenLatency,
                EvaluationLatency: cumulativeEvalLatency,
                AcceptanceLatency: acceptanceLatency,
                TotalLatency: totalStopwatch.Elapsed
            );

            if (acceptanceResult.Status == JobExecutionStatus.Completed)
            {
                if (isIdentityPassed)
                {
                    _metrics.RecordGenerationCompleted(job.Id, attempt, timing);
                }
                else
                {
                    _metrics.RecordGenerationQuarantined(job.Id, attempt, lastEvaluation?.IdentitySimilarity, lastEvaluation?.FeatureScore);
                    _metrics.RecordTiming(timing);
                }
            }

            _logger.LogInformation("[SceneGenerationJobFinished] OutboxId={OutboxId}, JobId={JobId}, Revision={Revision}, Attempts={Attempts}/{MaxAttempts}, TotalMs={TotalMs:F1}, GenMs={GenMs:F1}, EvalMs={EvalMs:F1}, Status={Status}",
                outboxId, job.Id, snapshot.SceneRevision, attempt, maxAttempts, totalStopwatch.ElapsedMilliseconds, cumulativeGenLatency.TotalMilliseconds, cumulativeEvalLatency.TotalMilliseconds, acceptanceResult.Status);

            return acceptanceResult;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[SceneGenerationCancelled] Execution cancelled for JobId={JobId}. Lease left to expire for recovery.", job.Id);
            throw;
        }
        catch (GpuNonTransientException ex)
        {
            totalStopwatch.Stop();
            _metrics.RecordGenerationFailed(job.Id, GenerationFailureCategory.InvalidWorkflow, attempt, totalStopwatch.Elapsed);
            _logger.LogError(ex, "[SceneGenerationFatalError] Non-transient failure for JobId={JobId}, OutboxId={OutboxId}: {Message}", job.Id, outboxId, ex.Message);
            var failTime = _dateTimeProvider.UtcNow;
            try
            {
                if (_dbContext.Database.IsRelational())
                {
                    await _dbContext.ImageGenerationJobs
                        .Where(j => j.Id == job.Id
                                    && j.ClaimedBy == workerId
                                    && j.Version == job.Version
                                    && (j.Status == ImageJobStatus.Processing || j.Status == ImageJobStatus.Evaluating)
                                    && j.LeaseUntil.HasValue
                                    && j.LeaseUntil.Value > failTime)
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
            totalStopwatch.Stop();
            var category = GenerationFailureClassifier.Classify(ex);
            _metrics.RecordGenerationFailed(job.Id, category, attempt, totalStopwatch.Elapsed);
            _logger.LogWarning(ex, "[SceneGenerationTransientError] Transient failure for JobId={JobId}, OutboxId={OutboxId}: {Message}. Releasing lease for retry.", job.Id, outboxId, ex.Message);
            var failTime = _dateTimeProvider.UtcNow;
            try
            {
                if (_dbContext.Database.IsRelational())
                {
                    await _dbContext.ImageGenerationJobs
                        .Where(j => j.Id == job.Id
                                    && j.ClaimedBy == workerId
                                    && j.Version == job.Version
                                    && (j.Status == ImageJobStatus.Processing || j.Status == ImageJobStatus.Evaluating)
                                    && j.LeaseUntil.HasValue
                                    && j.LeaseUntil.Value > failTime)
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
