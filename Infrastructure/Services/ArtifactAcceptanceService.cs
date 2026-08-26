using Application.Common;
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

/// <summary>
/// Service executing atomic compare-and-swap acceptance fencing, artifact lineage promotion,
/// and outbox event persistence in one single unit of work / relational transaction (P0-1).
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
        var (job, winningAttempt, snapshot, imageUrl, compiledPrompt, resolvedPreviousSceneImageUrl,
            fingerprint, metadataJson, isIdentityPassed, workerId, outboxId) = request;

        var liveUtc = _dateTimeProvider.UtcNow;
        var acceptedAttemptId = winningAttempt.Id;
        var workflow = snapshot.GenerationProfile?.Workflow ?? "VisualIdentity";
        var workflowVersion = snapshot.GenerationProfile?.WorkflowVersion ?? 1;

        if (_dbContext.Database.IsRelational())
        {
            try
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);

                var targetJobStatus = isIdentityPassed ? ImageJobStatus.Completed : ImageJobStatus.Quarantined;

                // 1. Atomic Compare-And-Swap (CAS) Fencing on ImageGenerationJobs
                var rowsAffected = await _dbContext.ImageGenerationJobs
                    .Where(j => j.Id == job.Id
                                && j.ClaimedBy == workerId
                                && j.Version == job.Version
                                && (j.Status == ImageJobStatus.Processing || j.Status == ImageJobStatus.Evaluating)
                                && j.AcceptedAttemptId == null
                                && j.LeaseUntil.HasValue
                                && j.LeaseUntil.Value > liveUtc)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.AcceptedAttemptId, acceptedAttemptId)
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

                // 2. Artifact Promotion / Demotion within same transaction boundary
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
                    imageUrl: imageUrl,
                    prompt: compiledPrompt,
                    generationRequestId: job.GenerationRequestId,
                    generationJobId: job.Id,
                    identityReferenceUrl: snapshot.IdentityReferenceUrl,
                    previousSceneImageUrl: resolvedPreviousSceneImageUrl,
                    workflow: workflow,
                    workflowVersion: workflowVersion,
                    isCurrent: isIdentityPassed,
                    generationFingerprint: fingerprint
                );

                await _dbContext.SceneImages.AddAsync(artifact, ct);
                await _dbContext.SaveChangesAsync(ct);

                await transaction.CommitAsync(ct);

                _logger.LogInformation("[ArtifactAcceptanceService] Transaction committed. JobId={JobId}, AttemptId={AttemptId}, ArtifactId={ArtifactId}, IsCurrent={IsCurrent}",
                    job.Id, acceptedAttemptId, artifact.Id, isIdentityPassed);
            }
            catch (DbUpdateException ex)
            {
                bool isUnique = ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
                             || ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);
                if (isUnique)
                {
                    _logger.LogInformation("[ArtifactAcceptanceService] Concurrent worker already committed artifact for JobId={JobId}, Fingerprint={Fingerprint}. Returning Completed.",
                        job.Id, fingerprint);
                    return new JobExecutionResult(JobExecutionStatus.Completed, "Artifact committed by concurrent worker");
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
            var currentJob = await _dbContext.ImageGenerationJobs
                .AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == job.Id, ct);

            if (currentJob == null || 
                currentJob.ClaimedBy != workerId || 
                !currentJob.LeaseUntil.HasValue || 
                currentJob.LeaseUntil.Value <= liveUtc || 
                currentJob.Version != job.Version ||
                (currentJob.Status != ImageJobStatus.Processing && currentJob.Status != ImageJobStatus.Evaluating))
            {
                _logger.LogWarning("[ArtifactAcceptanceService] In-memory job validation failed for JobId={JobId}, WorkerId={WorkerId}. Discarding artifact.",
                    job.Id, workerId);
                return new JobExecutionResult(JobExecutionStatus.Deferred, "Worker lease expired or stolen during generation");
            }

            if (isIdentityPassed)
            {
                job.AcceptAttempt(acceptedAttemptId, liveUtc, metadataJson);

                var existingCurrentImages = await _dbContext.SceneImages
                    .Where(img => img.SessionId == snapshot.SessionId && img.SceneRevision == snapshot.SceneRevision && img.IsCurrent)
                    .ToListAsync(ct);

                foreach (var img in existingCurrentImages)
                {
                    img.DemoteCurrent();
                }
            }
            else
            {
                job.Quarantine(acceptedAttemptId, "Identity invariant threshold not met across maximum retry attempts", liveUtc);
            }

            var artifact = new SceneImage(
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
                generationFingerprint: fingerprint
            );

            await _dbContext.SceneImages.AddAsync(artifact, ct);
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
