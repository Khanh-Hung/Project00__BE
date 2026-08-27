using System.Text.Json;
using Application.Common;
using Application.DTOs;
using Application.Exceptions;
using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.Events;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Sole authoritative service executing atomic compare-and-swap acceptance fencing, artifact lineage promotion,
/// VisualSessionState advancement, and outbox event persistence in one single unit of work / relational transaction.
/// </summary>
public sealed class ArtifactAcceptanceService : IArtifactAcceptanceService
{
    private readonly ProjectDbContext _dbContext;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILogger<ArtifactAcceptanceService> _logger;

    public ArtifactAcceptanceService(
        ProjectDbContext dbContext,
        IDateTimeProvider dateTimeProvider,
        ILogger<ArtifactAcceptanceService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _dateTimeProvider = dateTimeProvider ?? throw new ArgumentNullException(nameof(dateTimeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<JobExecutionResult> AcceptAttemptAtomicallyAsync(
        ArtifactAcceptanceRequest request,
        CancellationToken ct = default)
    {
        var (jobId, winningAttemptId, snapshot, imageUrl, compiledPrompt, resolvedPreviousSceneImageUrl,
            fingerprint, metadataJson, isIdentityPassed, workerId, outboxId, provenance) = request;

        // 1. Authoritative DB Lookup of Job and Winning Attempt
        var job = await _dbContext.ImageGenerationJobs
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

        if (job == null)
        {
            throw new InvalidOperationException($"Cannot accept attempt: Job {jobId} does not exist in database.");
        }

        if (job.CancellationRequested)
        {
            _logger.LogWarning("[ArtifactAcceptanceService] JobId={JobId} was flagged for cancellation. Rejecting artifact acceptance.", job.Id);
            return new JobExecutionResult(JobExecutionStatus.Deferred, "Job was cancelled");
        }

        var winningAttempt = await _dbContext.ImageGenerationAttempts
            .FirstOrDefaultAsync(a => a.Id == winningAttemptId, ct);

        if (winningAttempt == null)
        {
            throw new InvalidOperationException($"Cannot accept attempt: Attempt {winningAttemptId} does not exist in database.");
        }

        // Invariant P1-2: Winning attempt must strictly belong to the target Job
        if (winningAttempt.GenerationJobId != job.Id)
        {
            throw new InvalidOperationException($"Attempt {winningAttempt.Id} belongs to Job {winningAttempt.GenerationJobId}, not target Job {job.Id}.");
        }

        var liveUtc = _dateTimeProvider.UtcNow;

        // Defense-in-depth: Validate Attempt status, worker ownership, and active lease
        if (winningAttempt.Status != GenerationAttemptStatus.Succeeded
            && winningAttempt.Status != GenerationAttemptStatus.Degraded
            && winningAttempt.Status != GenerationAttemptStatus.Quarantined
            && winningAttempt.Status != GenerationAttemptStatus.Evaluating
            && winningAttempt.Status != GenerationAttemptStatus.Running)
        {
            throw new InvalidOperationException($"Attempt {winningAttempt.Id} is in invalid status {winningAttempt.Status} for acceptance.");
        }

        if (winningAttempt.Status == GenerationAttemptStatus.Running || winningAttempt.Status == GenerationAttemptStatus.Evaluating)
        {
            if (winningAttempt.ClaimedBy != null && !string.Equals(winningAttempt.ClaimedBy, workerId, StringComparison.Ordinal))
            {
                _logger.LogWarning("[ArtifactAcceptanceService] Active attempt {AttemptId} is owned by '{ClaimedBy}', not worker '{WorkerId}'. Rejecting acceptance.",
                    winningAttempt.Id, winningAttempt.ClaimedBy, workerId);
                return new JobExecutionResult(JobExecutionStatus.Deferred, "Attempt is owned by another worker");
            }

            if (winningAttempt.LeaseUntil.HasValue && winningAttempt.LeaseUntil.Value <= liveUtc)
            {
                _logger.LogWarning("[ArtifactAcceptanceService] Active attempt {AttemptId} lease expired at {LeaseUntil:O} (now: {Now:O}). Rejecting acceptance.",
                    winningAttempt.Id, winningAttempt.LeaseUntil.Value, liveUtc);
                return new JobExecutionResult(JobExecutionStatus.Deferred, "Attempt worker lease expired before acceptance");
            }
        }

        var attemptId = winningAttempt.Id;
        var workflow = snapshot.GenerationProfile?.Workflow ?? "VisualIdentity";
        var workflowVersion = snapshot.GenerationProfile?.WorkflowVersion ?? 1;

        if (_dbContext.Database.IsRelational())
        {
            try
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);

                var targetJobStatus = isIdentityPassed ? ImageJobStatus.Completed : ImageJobStatus.Quarantined;
                Guid? acceptedAttemptId = isIdentityPassed ? attemptId : null;
                Guid? quarantinedAttemptId = isIdentityPassed ? null : attemptId;

                // 1. Atomic Compare-And-Swap (CAS) Fencing on ImageGenerationJobs
                var rowsAffected = await _dbContext.ImageGenerationJobs
                    .Where(j => j.Id == job.Id
                                && j.ClaimedBy == workerId
                                && j.Version == job.Version
                                && (j.Status == ImageJobStatus.Processing || j.Status == ImageJobStatus.Evaluating)
                                && j.AcceptedAttemptId == null
                                && !j.CancellationRequested
                                && j.LeaseUntil.HasValue
                                && j.LeaseUntil.Value > liveUtc)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.AcceptedAttemptId, acceptedAttemptId)
                        .SetProperty(j => j.QuarantinedAttemptId, quarantinedAttemptId)
                        .SetProperty(j => j.Status, targetJobStatus)
                        .SetProperty(j => j.CompletedAt, liveUtc)
                        .SetProperty(j => j.GenerationMetadataJson, metadataJson)
                        .SetProperty(j => j.Version, j => j.Version + 1)
                        .SetProperty(j => j.UpdatedAt, liveUtc), ct);

                if (rowsAffected != 1)
                {
                    _logger.LogWarning("[ArtifactAcceptanceService] Atomic CAS acceptance fencing failed for JobId={JobId}, WorkerId={WorkerId}. Rows affected: {Rows}. Discarding artifact.",
                        job.Id, workerId, rowsAffected);
                    await transaction.RollbackAsync(ct);
                    return new JobExecutionResult(JobExecutionStatus.Deferred, "Worker lease lost or attempt already accepted by concurrent worker");
                }

                // 2. Predecessor resolution for lineage tracking
                Guid? predecessorArtifactId = snapshot.PredecessorSceneImageId;
                if (!predecessorArtifactId.HasValue && !string.IsNullOrWhiteSpace(resolvedPreviousSceneImageUrl))
                {
                    var predecessor = await _dbContext.SceneImages
                        .AsNoTracking()
                        .FirstOrDefaultAsync(img => img.ImageUrl == resolvedPreviousSceneImageUrl && img.SessionId == snapshot.SessionId, ct);
                    predecessorArtifactId = predecessor?.Id;
                }

                // 3. Visual Session State lookup & single authoritative revision assignment
                var sessionState = await _dbContext.VisualSessionStates
                    .FirstOrDefaultAsync(s => s.SessionId == snapshot.SessionId, ct);

                Guid? previousArtifactId = sessionState?.CurrentImageId;
                int assignedVisualRevision;

                // 4. Artifact Promotion / Demotion within same transaction boundary
                var existingArtifact = await _dbContext.SceneImages
                    .FirstOrDefaultAsync(img => (img.SessionId == snapshot.SessionId && img.GenerationRequestId == job.GenerationRequestId)
                                                || (fingerprint != null && img.GenerationFingerprint == fingerprint), ct);

                SceneImage artifact;
                if (existingArtifact != null)
                {
                    if (isIdentityPassed)
                    {
                        await _dbContext.SceneImages
                            .Where(img => img.SessionId == snapshot.SessionId && img.IsCurrent && img.Id != existingArtifact.Id)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(img => img.IsCurrent, false)
                                .SetProperty(img => img.LifecycleStatus, ArtifactLifecycleStatus.Historical)
                                .SetProperty(img => img.UpdatedAt, liveUtc), ct);

                        if (sessionState != null)
                        {
                            assignedVisualRevision = sessionState.PromoteArtifact(existingArtifact.Id, job.Id, liveUtc);
                        }
                        else
                        {
                            assignedVisualRevision = 1;
                            sessionState = new VisualSessionState(snapshot.SessionId, existingArtifact.Id, job.Id, visualRevision: 1, liveUtc);
                            await _dbContext.VisualSessionStates.AddAsync(sessionState, ct);
                        }

                        existingArtifact.PromoteToCurrent(assignedVisualRevision, predecessorArtifactId);
                    }
                    else
                    {
                        existingArtifact.MarkQuarantined(liveUtc);
                        assignedVisualRevision = sessionState?.VisualRevision ?? 1;
                    }

                    if (request.Provenance != null)
                    {
                        existingArtifact.AttachProvenance(request.Provenance);
                    }

                    artifact = existingArtifact;
                }
                else
                {
                    var newArtifactId = Guid.CreateVersion7();

                    if (isIdentityPassed)
                    {
                        await _dbContext.SceneImages
                            .Where(img => img.SessionId == snapshot.SessionId && img.IsCurrent)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(img => img.IsCurrent, false)
                                .SetProperty(img => img.LifecycleStatus, ArtifactLifecycleStatus.Historical)
                                .SetProperty(img => img.UpdatedAt, liveUtc), ct);

                        if (sessionState != null)
                        {
                            assignedVisualRevision = sessionState.PromoteArtifact(newArtifactId, job.Id, liveUtc);
                        }
                        else
                        {
                            assignedVisualRevision = 1;
                            sessionState = new VisualSessionState(snapshot.SessionId, newArtifactId, job.Id, visualRevision: 1, liveUtc);
                            await _dbContext.VisualSessionStates.AddAsync(sessionState, ct);
                        }
                    }
                    else
                    {
                        assignedVisualRevision = sessionState?.VisualRevision ?? 1;
                    }

                    artifact = new SceneImage(
                        sessionId: snapshot.SessionId,
                        characterId: snapshot.CharacterId,
                        turnId: snapshot.TurnId,
                        sceneRevision: snapshot.SceneRevision,
                        imageUrl: imageUrl,
                        prompt: compiledPrompt,
                        generationRequestId: job.GenerationRequestId,
                        generationJobId: job.Id,
                        identityReferenceUrl: snapshot.IdentityReferenceUrl,
                        previousSceneImageUrl: resolvedPreviousSceneImageUrl,
                        workflow: workflow,
                        workflowVersion: workflowVersion,
                        isCurrent: isIdentityPassed,
                        generationFingerprint: fingerprint,
                        provenanceJson: request.Provenance?.ToJson(),
                        predecessorArtifactId: predecessorArtifactId,
                        visualRevision: assignedVisualRevision,
                        lifecycleStatus: isIdentityPassed ? ArtifactLifecycleStatus.Current : ArtifactLifecycleStatus.Quarantined,
                        quarantinedAt: isIdentityPassed ? null : liveUtc,
                        id: newArtifactId
                    );

                    await _dbContext.SceneImages.AddAsync(artifact, ct);
                }

                // 5. Outbox Lifecycle Domain Event Persistence in the SAME Relational Transaction
                var outboxPayloadJson = isIdentityPassed
                    ? JsonSerializer.Serialize(new GenerationJobAcceptedEvent(
                        JobId: job.Id,
                        AcceptedAttemptId: attemptId,
                        ArtifactId: artifact.Id,
                        ImageUrl: imageUrl,
                        IsCurrent: true
                    ))
                    : JsonSerializer.Serialize(new GenerationJobQuarantinedEvent(
                        JobId: job.Id,
                        LastAttemptId: attemptId,
                        Reason: "Identity invariant threshold not met across maximum retry attempts"
                    ));

                var outboxEventType = isIdentityPassed
                    ? OutboxEventTypes.GenerationJobAccepted
                    : OutboxEventTypes.GenerationJobQuarantined;

                var lifecycleOutboxMsg = new OutboxMessage(
                    eventType: outboxEventType,
                    payloadJson: outboxPayloadJson
                );

                await _dbContext.OutboxMessages.AddAsync(lifecycleOutboxMsg, ct);

                if (isIdentityPassed)
                {
                    var acceptedDomainEvent = new VisualArtifactAccepted(
                        SessionId: snapshot.SessionId,
                        TurnId: snapshot.TurnId,
                        ArtifactId: artifact.Id,
                        GenerationJobId: job.Id,
                        VisualRevision: assignedVisualRevision,
                        OccurredAt: liveUtc
                    );

                    await _dbContext.OutboxMessages.AddAsync(new OutboxMessage(
                        eventType: "VisualArtifactAccepted",
                        payloadJson: JsonSerializer.Serialize(acceptedDomainEvent)
                    ), ct);

                    if (previousArtifactId.HasValue && previousArtifactId.Value != artifact.Id)
                    {
                        var supersededDomainEvent = new VisualArtifactSuperseded(
                            SessionId: snapshot.SessionId,
                            TurnId: snapshot.TurnId,
                            PreviousArtifactId: previousArtifactId.Value,
                            NewArtifactId: artifact.Id,
                            NewVisualRevision: assignedVisualRevision,
                            OccurredAt: liveUtc
                        );

                        await _dbContext.OutboxMessages.AddAsync(new OutboxMessage(
                            eventType: "VisualArtifactSuperseded",
                            payloadJson: JsonSerializer.Serialize(supersededDomainEvent)
                        ), ct);
                    }
                }

                await _dbContext.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                _logger.LogInformation("[ArtifactAcceptanceService] Transaction committed. JobId={JobId}, AttemptId={AttemptId}, ArtifactId={ArtifactId}, IsCurrent={IsCurrent}, VisualRevision={VisualRevision}",
                    job.Id, attemptId, artifact.Id, isIdentityPassed, assignedVisualRevision);
            }
            catch (DbUpdateException ex)
            {
                bool isUnique = ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
                             || ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);

                if (isUnique)
                {
                    var dbJob = await _dbContext.ImageGenerationJobs
                        .AsNoTracking()
                        .FirstOrDefaultAsync(j => j.Id == job.Id, ct);

                    var dbArtifact = await _dbContext.SceneImages
                        .AsNoTracking()
                        .FirstOrDefaultAsync(img => img.GenerationFingerprint == fingerprint, ct);

                    if (dbJob != null && (dbJob.Status == ImageJobStatus.Completed || dbJob.Status == ImageJobStatus.Quarantined) && dbArtifact != null)
                    {
                        _logger.LogInformation("[ArtifactAcceptanceService] Verified concurrent worker already committed artifact {ArtifactId} and completed JobId={JobId}. Returning Completed.",
                            dbArtifact.Id, job.Id);
                        return new JobExecutionResult(JobExecutionStatus.Completed, "Artifact committed and job completed by concurrent worker");
                    }

                    _logger.LogWarning("[ArtifactAcceptanceService] Unique constraint violation on JobId={JobId}, Fingerprint={Fingerprint}, but peer has not finished job. Returning Deferred.",
                        job.Id, fingerprint);
                    return new JobExecutionResult(JobExecutionStatus.Deferred, "Unique constraint conflict during artifact commit");
                }

                bool isTransient = DbExceptionClassifier.IsTransient(ex);
                if (isTransient)
                {
                    _logger.LogWarning(ex, "[ArtifactAcceptanceService] Transient relational transaction conflict for JobId={JobId}, WorkerId={WorkerId}. Discarding artifact.",
                        job.Id, workerId);
                    return new JobExecutionResult(JobExecutionStatus.Deferred, "Relational transaction conflict during artifact commit");
                }
                _logger.LogError(ex, "[ArtifactAcceptanceService] Permanent database update failure for JobId={JobId}, WorkerId={WorkerId}.", job.Id, workerId);
                throw new GpuNonTransientException($"Permanent database error: {ex.Message}", statusCode: null, innerException: ex);
            }
            catch (System.Data.Common.DbException ex)
            {
                bool isTransient = DbExceptionClassifier.IsTransient(ex);
                if (isTransient)
                {
                    _logger.LogWarning(ex, "[ArtifactAcceptanceService] Transient database connection conflict for JobId={JobId}, WorkerId={WorkerId}. Discarding artifact.",
                        job.Id, workerId);
                    return new JobExecutionResult(JobExecutionStatus.Deferred, "Database transaction conflict during artifact commit");
                }
                _logger.LogError(ex, "[ArtifactAcceptanceService] Permanent database connection failure for JobId={JobId}, WorkerId={WorkerId}.", job.Id, workerId);
                throw new GpuNonTransientException($"Permanent database error: {ex.Message}", statusCode: null, innerException: ex);
            }
        }
        else
        {
            // Non-relational (in-memory) test harness path
            var currentJob = await _dbContext.ImageGenerationJobs
                .AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == job.Id, ct);

            if (currentJob == null || 
                currentJob.ClaimedBy != workerId || 
                !currentJob.LeaseUntil.HasValue || 
                currentJob.LeaseUntil.Value <= liveUtc || 
                currentJob.Version != job.Version ||
                currentJob.CancellationRequested ||
                (currentJob.Status != ImageJobStatus.Processing && currentJob.Status != ImageJobStatus.Evaluating))
            {
                _logger.LogWarning("[ArtifactAcceptanceService] In-memory job validation failed for JobId={JobId}, WorkerId={WorkerId}. Discarding artifact.",
                    job.Id, workerId);
                return new JobExecutionResult(JobExecutionStatus.Deferred, "Worker lease expired, stolen, or job was cancelled during generation");
            }

            if (isIdentityPassed)
            {
                job.AcceptAttempt(attemptId, liveUtc, workerId, metadataJson);
            }
            else
            {
                job.Quarantine(attemptId, "Identity invariant threshold not met across maximum retry attempts", liveUtc, workerId);
            }

            Guid? predecessorArtifactId = snapshot.PredecessorSceneImageId;
            if (!predecessorArtifactId.HasValue && !string.IsNullOrWhiteSpace(resolvedPreviousSceneImageUrl))
            {
                var predecessor = await _dbContext.SceneImages
                    .AsNoTracking()
                    .FirstOrDefaultAsync(img => img.ImageUrl == resolvedPreviousSceneImageUrl && img.SessionId == snapshot.SessionId, ct);
                predecessorArtifactId = predecessor?.Id;
            }

            var sessionState = await _dbContext.VisualSessionStates
                .FirstOrDefaultAsync(s => s.SessionId == snapshot.SessionId, ct);

            Guid? previousArtifactId = sessionState?.CurrentImageId;
            int assignedVisualRevision;

            var existingArtifact = await _dbContext.SceneImages
                .FirstOrDefaultAsync(img => (img.SessionId == snapshot.SessionId && img.GenerationRequestId == job.GenerationRequestId)
                                            || (fingerprint != null && img.GenerationFingerprint == fingerprint), ct);

            SceneImage artifact;
            if (existingArtifact != null)
            {
                if (isIdentityPassed)
                {
                    var otherCurrent = await _dbContext.SceneImages
                        .Where(img => img.SessionId == snapshot.SessionId && img.IsCurrent && img.Id != existingArtifact.Id)
                        .ToListAsync(ct);
                    foreach (var img in otherCurrent)
                    {
                        img.DemoteCurrent();
                    }

                    if (sessionState != null)
                    {
                        assignedVisualRevision = sessionState.PromoteArtifact(existingArtifact.Id, job.Id, liveUtc);
                    }
                    else
                    {
                        assignedVisualRevision = 1;
                        sessionState = new VisualSessionState(snapshot.SessionId, existingArtifact.Id, job.Id, visualRevision: 1, liveUtc);
                        await _dbContext.VisualSessionStates.AddAsync(sessionState, ct);
                    }

                    existingArtifact.PromoteToCurrent(assignedVisualRevision, predecessorArtifactId);
                }
                else
                {
                    existingArtifact.MarkQuarantined(liveUtc);
                    assignedVisualRevision = sessionState?.VisualRevision ?? 1;
                }

                if (request.Provenance != null)
                {
                    existingArtifact.AttachProvenance(request.Provenance);
                }

                artifact = existingArtifact;
            }
            else
            {
                var newArtifactId = Guid.CreateVersion7();

                if (isIdentityPassed)
                {
                    var existingCurrentImages = await _dbContext.SceneImages
                        .Where(img => img.SessionId == snapshot.SessionId && img.IsCurrent)
                        .ToListAsync(ct);

                    foreach (var img in existingCurrentImages)
                    {
                        img.DemoteCurrent();
                    }

                    if (sessionState != null)
                    {
                        assignedVisualRevision = sessionState.PromoteArtifact(newArtifactId, job.Id, liveUtc);
                    }
                    else
                    {
                        assignedVisualRevision = 1;
                        sessionState = new VisualSessionState(snapshot.SessionId, newArtifactId, job.Id, visualRevision: 1, liveUtc);
                        await _dbContext.VisualSessionStates.AddAsync(sessionState, ct);
                    }
                }
                else
                {
                    assignedVisualRevision = sessionState?.VisualRevision ?? 1;
                }

                artifact = new SceneImage(
                    sessionId: snapshot.SessionId,
                    characterId: snapshot.CharacterId,
                    turnId: snapshot.TurnId,
                    sceneRevision: snapshot.SceneRevision,
                    imageUrl: imageUrl,
                    prompt: compiledPrompt,
                    generationRequestId: job.GenerationRequestId,
                    generationJobId: job.Id,
                    identityReferenceUrl: snapshot.IdentityReferenceUrl,
                    previousSceneImageUrl: resolvedPreviousSceneImageUrl,
                    workflow: workflow,
                    workflowVersion: workflowVersion,
                    isCurrent: isIdentityPassed,
                    generationFingerprint: fingerprint,
                    provenanceJson: request.Provenance?.ToJson(),
                    predecessorArtifactId: predecessorArtifactId,
                    visualRevision: assignedVisualRevision,
                    lifecycleStatus: isIdentityPassed ? ArtifactLifecycleStatus.Current : ArtifactLifecycleStatus.Quarantined,
                    quarantinedAt: isIdentityPassed ? null : liveUtc,
                    id: newArtifactId
                );

                await _dbContext.SceneImages.AddAsync(artifact, ct);
            }

            var outboxPayloadJson = isIdentityPassed
                ? JsonSerializer.Serialize(new GenerationJobAcceptedEvent(
                    JobId: job.Id,
                    AcceptedAttemptId: attemptId,
                    ArtifactId: artifact.Id,
                    ImageUrl: imageUrl,
                    IsCurrent: true
                ))
                : JsonSerializer.Serialize(new GenerationJobQuarantinedEvent(
                    JobId: job.Id,
                    LastAttemptId: attemptId,
                    Reason: "Identity invariant threshold not met across maximum retry attempts"
                ));

            var outboxEventType = isIdentityPassed
                ? OutboxEventTypes.GenerationJobAccepted
                : OutboxEventTypes.GenerationJobQuarantined;

            var lifecycleOutboxMsg = new OutboxMessage(
                eventType: outboxEventType,
                payloadJson: outboxPayloadJson
            );

            await _dbContext.OutboxMessages.AddAsync(lifecycleOutboxMsg, ct);

            try
            {
                await _dbContext.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(ex, "[ArtifactAcceptanceService] In-memory concurrency conflict for JobId={JobId}. Discarding artifact.", job.Id);
                return new JobExecutionResult(JobExecutionStatus.Deferred, "Job updated concurrently during commit");
            }
        }

        return new JobExecutionResult(JobExecutionStatus.Completed);
    }
}
