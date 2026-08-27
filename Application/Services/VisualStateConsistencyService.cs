using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Services;

public sealed class VisualStateConsistencyService : IVisualStateConsistencyService
{
    private readonly ProjectDbContext _dbContext;
    private readonly ILogger<VisualStateConsistencyService> _logger;

    public VisualStateConsistencyService(
        ProjectDbContext dbContext,
        ILogger<VisualStateConsistencyService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ArtifactConsistencyResult> ValidateConsistencyAsync(Guid sessionId, CancellationToken ct = default)
    {
        var sessionState = await _dbContext.VisualSessionStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);

        // 1. Resolve Authoritative Completed Job (Revision-based, not CreatedAt heuristic)
        ImageGenerationJob? authoritativeJob = null;

        if (sessionState?.CurrentGenerationJobId.HasValue == true)
        {
            authoritativeJob = await _dbContext.ImageGenerationJobs
                .AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == sessionState.CurrentGenerationJobId.Value && j.SessionId == sessionId, ct);
        }

        if (authoritativeJob == null)
        {
            authoritativeJob = await _dbContext.ImageGenerationJobs
                .AsNoTracking()
                .Where(j => j.SessionId == sessionId && j.Status == ImageJobStatus.Completed && j.AcceptedAttemptId.HasValue)
                .OrderByDescending(j => j.SceneRevision)
                .ThenByDescending(j => j.Id)
                .FirstOrDefaultAsync(ct);
        }

        // 2. Case: No completed generation job exists
        if (authoritativeJob == null)
        {
            if (sessionState == null || !sessionState.CurrentImageId.HasValue)
            {
                return new ArtifactConsistencyResult(
                    Status: VisualStateConsistencyStatus.Healthy,
                    SessionId: sessionId,
                    CurrentArtifactId: null,
                    ExpectedArtifactId: null,
                    Reason: "Empty session with no completed visual generation jobs."
                );
            }

            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Corrupted,
                SessionId: sessionId,
                CurrentArtifactId: sessionState.CurrentImageId,
                ExpectedArtifactId: null,
                Reason: "VisualSessionState references an artifact, but no completed generation job exists for this session."
            );
        }

        // 3. Validate Authoritative Job Invariants
        if (authoritativeJob.Status != ImageJobStatus.Completed || !authoritativeJob.AcceptedAttemptId.HasValue)
        {
            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Corrupted,
                SessionId: sessionId,
                CurrentArtifactId: sessionState?.CurrentImageId,
                ExpectedArtifactId: null,
                Reason: $"Completed job '{authoritativeJob.Id}' has no recorded AcceptedAttemptId.",
                ExpectedJobId: authoritativeJob.Id
            );
        }

        // 4. Traverse Authoritative Attempt Ledger: Job.AcceptedAttemptId -> Winning Attempt
        var winningAttempt = await _dbContext.ImageGenerationAttempts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == authoritativeJob.AcceptedAttemptId.Value && a.GenerationJobId == authoritativeJob.Id, ct);

        if (winningAttempt == null)
        {
            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Corrupted,
                SessionId: sessionId,
                CurrentArtifactId: sessionState?.CurrentImageId,
                ExpectedArtifactId: null,
                Reason: $"Job '{authoritativeJob.Id}' references AcceptedAttemptId '{authoritativeJob.AcceptedAttemptId.Value}' which does not exist in ledger.",
                ExpectedJobId: authoritativeJob.Id
            );
        }

        if (winningAttempt.Status != GenerationAttemptStatus.Succeeded)
        {
            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Corrupted,
                SessionId: sessionId,
                CurrentArtifactId: sessionState?.CurrentImageId,
                ExpectedArtifactId: null,
                Reason: $"Winning attempt '{winningAttempt.Id}' is in non-succeeded status '{winningAttempt.Status}'.",
                ExpectedJobId: authoritativeJob.Id
            );
        }

        if (!winningAttempt.AcceptedArtifactId.HasValue)
        {
            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Corrupted,
                SessionId: sessionId,
                CurrentArtifactId: sessionState?.CurrentImageId,
                ExpectedArtifactId: null,
                Reason: $"Winning attempt '{winningAttempt.Id}' has null AcceptedArtifactId.",
                ExpectedJobId: authoritativeJob.Id
            );
        }

        // 5. Direct FK Traversal: Attempt.AcceptedArtifactId -> SceneImage.Id (Zero heuristic fallback)
        var authoritativeArtifact = await _dbContext.SceneImages
            .AsNoTracking()
            .FirstOrDefaultAsync(img => img.Id == winningAttempt.AcceptedArtifactId.Value, ct);

        if (authoritativeArtifact == null)
        {
            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Corrupted,
                SessionId: sessionId,
                CurrentArtifactId: sessionState?.CurrentImageId,
                ExpectedArtifactId: null,
                Reason: $"AcceptedArtifactId '{winningAttempt.AcceptedArtifactId.Value}' does not exist in database storage.",
                ExpectedJobId: authoritativeJob.Id
            );
        }

        // 6. Full Bidirectional Lineage Verification
        if (authoritativeArtifact.SessionId != sessionId)
        {
            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Corrupted,
                SessionId: sessionId,
                CurrentArtifactId: sessionState?.CurrentImageId,
                ExpectedArtifactId: null,
                Reason: $"Authoritative artifact '{authoritativeArtifact.Id}' belongs to foreign session '{authoritativeArtifact.SessionId}'.",
                ExpectedJobId: authoritativeJob.Id
            );
        }

        if (authoritativeArtifact.GenerationAttemptId != winningAttempt.Id)
        {
            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Corrupted,
                SessionId: sessionId,
                CurrentArtifactId: sessionState?.CurrentImageId,
                ExpectedArtifactId: null,
                Reason: $"Lineage fork detected: Artifact '{authoritativeArtifact.Id}' has GenerationAttemptId '{authoritativeArtifact.GenerationAttemptId}' vs Attempt '{winningAttempt.Id}'.",
                ExpectedJobId: authoritativeJob.Id
            );
        }

        if (authoritativeArtifact.GenerationJobId.HasValue && authoritativeArtifact.GenerationJobId.Value != authoritativeJob.Id)
        {
            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Corrupted,
                SessionId: sessionId,
                CurrentArtifactId: sessionState?.CurrentImageId,
                ExpectedArtifactId: null,
                Reason: $"Lineage fork detected: Artifact '{authoritativeArtifact.Id}' has GenerationJobId '{authoritativeArtifact.GenerationJobId}' vs Job '{authoritativeJob.Id}'.",
                ExpectedJobId: authoritativeJob.Id
            );
        }

        if (authoritativeArtifact.LifecycleStatus == ArtifactLifecycleStatus.Deleted)
        {
            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Corrupted,
                SessionId: sessionId,
                CurrentArtifactId: sessionState?.CurrentImageId,
                ExpectedArtifactId: null,
                Reason: $"Authoritative artifact '{authoritativeArtifact.Id}' has lifecycle status Deleted.",
                ExpectedJobId: authoritativeJob.Id
            );
        }

        if (authoritativeArtifact.LifecycleStatus == ArtifactLifecycleStatus.Quarantined)
        {
            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Corrupted,
                SessionId: sessionId,
                CurrentArtifactId: sessionState?.CurrentImageId,
                ExpectedArtifactId: null,
                Reason: $"Authoritative artifact '{authoritativeArtifact.Id}' has lifecycle status Quarantined.",
                ExpectedJobId: authoritativeJob.Id
            );
        }

        // 7. Session State Lineage Alignment Evaluation
        if (sessionState == null)
        {
            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Repairable,
                SessionId: sessionId,
                CurrentArtifactId: null,
                ExpectedArtifactId: authoritativeArtifact.Id,
                Reason: "VisualSessionState entity is missing, but full authoritative accepted artifact chain exists.",
                ExpectedJobId: authoritativeJob.Id,
                ExpectedVisualRevision: authoritativeArtifact.VisualRevision
            );
        }

        if (!sessionState.CurrentImageId.HasValue)
        {
            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Repairable,
                SessionId: sessionId,
                CurrentArtifactId: null,
                ExpectedArtifactId: authoritativeArtifact.Id,
                Reason: "VisualSessionState has null pointer, but full authoritative accepted artifact chain exists.",
                ExpectedJobId: authoritativeJob.Id,
                ExpectedVisualRevision: authoritativeArtifact.VisualRevision
            );
        }

        if (sessionState.CurrentImageId.Value != authoritativeArtifact.Id)
        {
            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Corrupted,
                SessionId: sessionId,
                CurrentArtifactId: sessionState.CurrentImageId.Value,
                ExpectedArtifactId: authoritativeArtifact.Id,
                Reason: $"VisualSessionState points to artifact '{sessionState.CurrentImageId.Value}' but authoritative attempt ledger points to '{authoritativeArtifact.Id}'.",
                ExpectedJobId: authoritativeJob.Id,
                ExpectedVisualRevision: authoritativeArtifact.VisualRevision
            );
        }

        if (authoritativeArtifact.VisualRevision != sessionState.VisualRevision)
        {
            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Corrupted,
                SessionId: sessionId,
                CurrentArtifactId: sessionState.CurrentImageId.Value,
                ExpectedArtifactId: authoritativeArtifact.Id,
                Reason: $"VisualRevision mismatch: Artifact revision {authoritativeArtifact.VisualRevision} vs SessionState revision {sessionState.VisualRevision}.",
                ExpectedJobId: authoritativeJob.Id,
                ExpectedVisualRevision: authoritativeArtifact.VisualRevision
            );
        }

        if (!authoritativeArtifact.IsCurrent || authoritativeArtifact.LifecycleStatus != ArtifactLifecycleStatus.Current)
        {
            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Inconsistent,
                SessionId: sessionId,
                CurrentArtifactId: sessionState.CurrentImageId.Value,
                ExpectedArtifactId: authoritativeArtifact.Id,
                Reason: $"Authoritative artifact '{authoritativeArtifact.Id}' is not marked as IsCurrent = true (Status: {authoritativeArtifact.LifecycleStatus}).",
                ExpectedJobId: authoritativeJob.Id,
                ExpectedVisualRevision: authoritativeArtifact.VisualRevision
            );
        }

        // 8. Perfect lineage and state alignment
        return new ArtifactConsistencyResult(
            Status: VisualStateConsistencyStatus.Healthy,
            SessionId: sessionId,
            CurrentArtifactId: authoritativeArtifact.Id,
            ExpectedArtifactId: authoritativeArtifact.Id,
            Reason: "Visual session state and bidirectional lineage are fully consistent.",
            ExpectedJobId: authoritativeJob.Id,
            ExpectedVisualRevision: authoritativeArtifact.VisualRevision
        );
    }

    public async Task<ArtifactConsistencyResult> RepairVisualStateAsync(Guid sessionId, CancellationToken ct = default)
    {
        var diagnosis = await ValidateConsistencyAsync(sessionId, ct);

        if (diagnosis.Status == VisualStateConsistencyStatus.Healthy)
        {
            return diagnosis;
        }

        if (diagnosis.Status != VisualStateConsistencyStatus.Repairable || !diagnosis.ExpectedArtifactId.HasValue || !diagnosis.ExpectedJobId.HasValue)
        {
            throw new InvalidOperationException(
                $"Cannot repair visual state for session '{sessionId}': State is {diagnosis.Status} ({diagnosis.Reason}). Full authoritative provenance chain required.");
        }

        var targetJobId = diagnosis.ExpectedJobId.Value;
        var targetArtifactId = diagnosis.ExpectedArtifactId.Value;
        var expectedRevision = diagnosis.ExpectedVisualRevision ?? 1;
        var now = DateTime.UtcNow;

        if (_dbContext.Database.IsRelational())
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);

            // 1. Optimistic concurrency & version fence against newer visual revisions and job mismatches
            var sessionState = await _dbContext.VisualSessionStates
                .FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);

            if (sessionState != null)
            {
                if (sessionState.VisualRevision > expectedRevision)
                {
                    await transaction.RollbackAsync(ct);
                    throw new InvalidOperationException(
                        $"Cannot repair session '{sessionId}': Session state has advanced to a newer VisualRevision ({sessionState.VisualRevision} > {expectedRevision}). Repair aborted to prevent state rollback.");
                }

                if (sessionState.CurrentGenerationJobId.HasValue && sessionState.CurrentGenerationJobId.Value != targetJobId && sessionState.VisualRevision >= expectedRevision)
                {
                    await transaction.RollbackAsync(ct);
                    throw new InvalidOperationException(
                        $"Cannot repair session '{sessionId}': Session state points to a different authoritative job '{sessionState.CurrentGenerationJobId.Value}' at revision {sessionState.VisualRevision} (target job: '{targetJobId}'). Repair aborted to prevent state rollback.");
                }
            }

            // 2. Full bidirectional re-verification of the entire chain inside the transaction
            var job = await _dbContext.ImageGenerationJobs
                .FirstOrDefaultAsync(j => j.Id == targetJobId && j.SessionId == sessionId, ct);

            if (job == null || job.Status != ImageJobStatus.Completed || !job.AcceptedAttemptId.HasValue)
            {
                await transaction.RollbackAsync(ct);
                throw new InvalidOperationException(
                    $"Cannot repair session '{sessionId}': Authoritative job '{targetJobId}' is invalid or missing accepted attempt.");
            }

            var attempt = await _dbContext.ImageGenerationAttempts
                .FirstOrDefaultAsync(a => a.Id == job.AcceptedAttemptId.Value && a.GenerationJobId == job.Id, ct);

            if (attempt == null || attempt.Status != GenerationAttemptStatus.Succeeded || attempt.AcceptedArtifactId != targetArtifactId)
            {
                await transaction.RollbackAsync(ct);
                throw new InvalidOperationException(
                    $"Cannot repair session '{sessionId}': Winning attempt verification failed for job '{job.Id}'.");
            }

            var targetArtifact = await _dbContext.SceneImages
                .FirstOrDefaultAsync(img => img.Id == targetArtifactId && img.SessionId == sessionId, ct);

            if (targetArtifact == null
                || targetArtifact.LifecycleStatus == ArtifactLifecycleStatus.Deleted
                || targetArtifact.LifecycleStatus == ArtifactLifecycleStatus.Quarantined
                || targetArtifact.GenerationAttemptId != attempt.Id
                || (targetArtifact.GenerationJobId.HasValue && targetArtifact.GenerationJobId.Value != job.Id))
            {
                await transaction.RollbackAsync(ct);
                throw new InvalidOperationException(
                    $"Cannot repair session '{sessionId}': Target artifact '{targetArtifactId}' lineage verification failed.");
            }

            // 3. Demote other current artifacts atomically
            await _dbContext.SceneImages
                .Where(img => img.SessionId == sessionId && img.IsCurrent && img.Id != targetArtifactId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(img => img.IsCurrent, false)
                    .SetProperty(img => img.LifecycleStatus, ArtifactLifecycleStatus.Historical)
                    .SetProperty(img => img.UpdatedAt, now), ct);

            targetArtifact.SetCurrent(true);

            if (sessionState != null)
            {
                sessionState.RestoreCurrent(targetArtifact.Id, job.Id, targetArtifact.VisualRevision, now);
            }
            else
            {
                sessionState = new VisualSessionState(sessionId, targetArtifact.Id, job.Id, targetArtifact.VisualRevision, now);
                await _dbContext.VisualSessionStates.AddAsync(sessionState, ct);
            }

            await _dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            _logger.LogInformation("[VisualStateConsistencyService] Atomically repaired VisualSessionState for SessionId={SessionId} to ArtifactId={ArtifactId} (Revision={Revision})",
                sessionId, targetArtifact.Id, targetArtifact.VisualRevision);

            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Healthy,
                SessionId: sessionId,
                CurrentArtifactId: targetArtifact.Id,
                ExpectedArtifactId: targetArtifact.Id,
                Reason: "Visual state was deterministically repaired from authoritative attempt ledger.",
                ExpectedJobId: job.Id,
                ExpectedVisualRevision: targetArtifact.VisualRevision
            );
        }
        else
        {
            // In-memory test harness path
            var sessionState = await _dbContext.VisualSessionStates
                .FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);

            if (sessionState != null)
            {
                if (sessionState.VisualRevision > expectedRevision)
                {
                    throw new InvalidOperationException(
                        $"Cannot repair session '{sessionId}': Session state has advanced to a newer VisualRevision ({sessionState.VisualRevision} > {expectedRevision}). Repair aborted to prevent state rollback.");
                }

                if (sessionState.CurrentGenerationJobId.HasValue && sessionState.CurrentGenerationJobId.Value != targetJobId && sessionState.VisualRevision >= expectedRevision)
                {
                    throw new InvalidOperationException(
                        $"Cannot repair session '{sessionId}': Session state points to a different authoritative job '{sessionState.CurrentGenerationJobId.Value}' at revision {sessionState.VisualRevision} (target job: '{targetJobId}'). Repair aborted to prevent state rollback.");
                }
            }

            var job = await _dbContext.ImageGenerationJobs
                .FirstOrDefaultAsync(j => j.Id == targetJobId && j.SessionId == sessionId, ct);

            if (job == null || job.Status != ImageJobStatus.Completed || !job.AcceptedAttemptId.HasValue)
            {
                throw new InvalidOperationException(
                    $"Cannot repair session '{sessionId}': Authoritative job '{targetJobId}' is invalid or missing accepted attempt.");
            }

            var attempt = await _dbContext.ImageGenerationAttempts
                .FirstOrDefaultAsync(a => a.Id == job.AcceptedAttemptId.Value && a.GenerationJobId == job.Id, ct);

            if (attempt == null || attempt.Status != GenerationAttemptStatus.Succeeded || attempt.AcceptedArtifactId != targetArtifactId)
            {
                throw new InvalidOperationException(
                    $"Cannot repair session '{sessionId}': Winning attempt verification failed for job '{job.Id}'.");
            }

            var targetArtifact = await _dbContext.SceneImages
                .FirstOrDefaultAsync(img => img.Id == targetArtifactId && img.SessionId == sessionId, ct);

            if (targetArtifact == null
                || targetArtifact.LifecycleStatus == ArtifactLifecycleStatus.Deleted
                || targetArtifact.LifecycleStatus == ArtifactLifecycleStatus.Quarantined
                || targetArtifact.GenerationAttemptId != attempt.Id
                || (targetArtifact.GenerationJobId.HasValue && targetArtifact.GenerationJobId.Value != job.Id))
            {
                throw new InvalidOperationException(
                    $"Cannot repair session '{sessionId}': Target artifact '{targetArtifactId}' lineage verification failed.");
            }

            var otherCurrent = await _dbContext.SceneImages
                .Where(img => img.SessionId == sessionId && img.IsCurrent && img.Id != targetArtifactId)
                .ToListAsync(ct);

            foreach (var img in otherCurrent)
            {
                img.DemoteCurrent();
            }

            targetArtifact.SetCurrent(true);

            if (sessionState != null)
            {
                sessionState.RestoreCurrent(targetArtifact.Id, job.Id, targetArtifact.VisualRevision, now);
            }
            else
            {
                sessionState = new VisualSessionState(sessionId, targetArtifact.Id, job.Id, targetArtifact.VisualRevision, now);
                await _dbContext.VisualSessionStates.AddAsync(sessionState, ct);
            }

            await _dbContext.SaveChangesAsync(ct);

            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Healthy,
                SessionId: sessionId,
                CurrentArtifactId: targetArtifact.Id,
                ExpectedArtifactId: targetArtifact.Id,
                Reason: "Visual state was deterministically repaired from authoritative attempt ledger.",
                ExpectedJobId: job.Id,
                ExpectedVisualRevision: targetArtifact.VisualRevision
            );
        }
    }
}
