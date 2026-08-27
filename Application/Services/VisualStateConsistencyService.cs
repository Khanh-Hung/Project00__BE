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

        // 1. Session state does not exist in database
        if (sessionState == null)
        {
            var latestCompletedJob = await _dbContext.ImageGenerationJobs
                .AsNoTracking()
                .Where(j => j.SessionId == sessionId && j.Status == ImageJobStatus.Completed && j.AcceptedAttemptId.HasValue)
                .OrderByDescending(j => j.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (latestCompletedJob == null)
            {
                // Clean empty session, no generation jobs yet
                return new ArtifactConsistencyResult(
                    Status: VisualStateConsistencyStatus.Healthy,
                    SessionId: sessionId,
                    CurrentArtifactId: null,
                    ExpectedArtifactId: null,
                    Reason: "Empty session with no visual generation jobs."
                );
            }

            var winningAttempt = await _dbContext.ImageGenerationAttempts
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == latestCompletedJob.AcceptedAttemptId!.Value, ct);

            SceneImage? authoritativeArtifact = null;
            if (winningAttempt?.AcceptedArtifactId.HasValue == true)
            {
                authoritativeArtifact = await _dbContext.SceneImages
                    .AsNoTracking()
                    .FirstOrDefaultAsync(img => img.Id == winningAttempt.AcceptedArtifactId.Value && img.SessionId == sessionId && img.LifecycleStatus != ArtifactLifecycleStatus.Deleted, ct);
            }

            if (authoritativeArtifact == null && winningAttempt != null && !string.IsNullOrWhiteSpace(winningAttempt.GenerationFingerprint))
            {
                authoritativeArtifact = await _dbContext.SceneImages
                    .AsNoTracking()
                    .FirstOrDefaultAsync(img => img.SessionId == sessionId && img.GenerationFingerprint == winningAttempt.GenerationFingerprint && img.LifecycleStatus != ArtifactLifecycleStatus.Deleted, ct);
            }

            if (authoritativeArtifact != null)
            {
                return new ArtifactConsistencyResult(
                    Status: VisualStateConsistencyStatus.Repairable,
                    SessionId: sessionId,
                    CurrentArtifactId: null,
                    ExpectedArtifactId: authoritativeArtifact.Id,
                    Reason: "VisualSessionState entity is missing, but authoritative accepted artifact exists in ledger."
                );
            }

            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Corrupted,
                SessionId: sessionId,
                CurrentArtifactId: null,
                ExpectedArtifactId: null,
                Reason: "VisualSessionState entity is missing and completed job has no resolvable artifact."
            );
        }

        // 2. Session state exists, but CurrentImageId is NULL
        if (!sessionState.CurrentImageId.HasValue)
        {
            var latestCompletedJob = await _dbContext.ImageGenerationJobs
                .AsNoTracking()
                .Where(j => j.SessionId == sessionId && j.Status == ImageJobStatus.Completed && j.AcceptedAttemptId.HasValue)
                .OrderByDescending(j => j.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (latestCompletedJob == null)
            {
                return new ArtifactConsistencyResult(
                    Status: VisualStateConsistencyStatus.Healthy,
                    SessionId: sessionId,
                    CurrentArtifactId: null,
                    ExpectedArtifactId: null,
                    Reason: "Session state has no current image and no completed generation jobs exist."
                );
            }

            var winningAttempt = await _dbContext.ImageGenerationAttempts
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == latestCompletedJob.AcceptedAttemptId!.Value, ct);

            SceneImage? authoritativeArtifact = null;
            if (winningAttempt?.AcceptedArtifactId.HasValue == true)
            {
                authoritativeArtifact = await _dbContext.SceneImages
                    .AsNoTracking()
                    .FirstOrDefaultAsync(img => img.Id == winningAttempt.AcceptedArtifactId.Value && img.SessionId == sessionId && img.LifecycleStatus != ArtifactLifecycleStatus.Deleted, ct);
            }

            if (authoritativeArtifact != null)
            {
                return new ArtifactConsistencyResult(
                    Status: VisualStateConsistencyStatus.Repairable,
                    SessionId: sessionId,
                    CurrentArtifactId: null,
                    ExpectedArtifactId: authoritativeArtifact.Id,
                    Reason: "VisualSessionState has null pointer, but authoritative accepted artifact exists in ledger."
                );
            }

            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Inconsistent,
                SessionId: sessionId,
                CurrentArtifactId: null,
                ExpectedArtifactId: null,
                Reason: "VisualSessionState has null pointer despite completed jobs."
            );
        }

        // 3. Session state has a non-null CurrentImageId
        var currentArtifactId = sessionState.CurrentImageId.Value;
        var artifact = await _dbContext.SceneImages
            .AsNoTracking()
            .FirstOrDefaultAsync(img => img.Id == currentArtifactId, ct);

        if (artifact == null)
        {
            // Try to see if authoritative attempt exists to allow repair
            var latestCompletedJob = await _dbContext.ImageGenerationJobs
                .AsNoTracking()
                .Where(j => j.SessionId == sessionId && j.Status == ImageJobStatus.Completed && j.AcceptedAttemptId.HasValue)
                .OrderByDescending(j => j.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (latestCompletedJob != null)
            {
                var winningAttempt = await _dbContext.ImageGenerationAttempts
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == latestCompletedJob.AcceptedAttemptId!.Value, ct);

                if (winningAttempt?.AcceptedArtifactId.HasValue == true)
                {
                    var altArtifact = await _dbContext.SceneImages
                        .AsNoTracking()
                        .FirstOrDefaultAsync(img => img.Id == winningAttempt.AcceptedArtifactId.Value && img.SessionId == sessionId && img.LifecycleStatus != ArtifactLifecycleStatus.Deleted, ct);

                    if (altArtifact != null)
                    {
                        return new ArtifactConsistencyResult(
                            Status: VisualStateConsistencyStatus.Repairable,
                            SessionId: sessionId,
                            CurrentArtifactId: currentArtifactId,
                            ExpectedArtifactId: altArtifact.Id,
                            Reason: "Current artifact ID missing in storage, but authoritative artifact exists on winning attempt."
                        );
                    }
                }
            }

            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Corrupted,
                SessionId: sessionId,
                CurrentArtifactId: currentArtifactId,
                ExpectedArtifactId: null,
                Reason: $"Current artifact '{currentArtifactId}' does not exist in database storage."
            );
        }

        // 4. Invariant checks on the resolved artifact
        if (artifact.SessionId != sessionId)
        {
            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Corrupted,
                SessionId: sessionId,
                CurrentArtifactId: currentArtifactId,
                ExpectedArtifactId: null,
                Reason: $"Current artifact '{currentArtifactId}' belongs to foreign session '{artifact.SessionId}'."
            );
        }

        if (artifact.LifecycleStatus == ArtifactLifecycleStatus.Deleted)
        {
            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Inconsistent,
                SessionId: sessionId,
                CurrentArtifactId: currentArtifactId,
                ExpectedArtifactId: null,
                Reason: $"Current artifact '{currentArtifactId}' has lifecycle status Deleted."
            );
        }

        if (artifact.LifecycleStatus == ArtifactLifecycleStatus.Quarantined)
        {
            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Corrupted,
                SessionId: sessionId,
                CurrentArtifactId: currentArtifactId,
                ExpectedArtifactId: null,
                Reason: $"Current artifact '{currentArtifactId}' has lifecycle status Quarantined and cannot be current."
            );
        }

        if (!artifact.IsCurrent || artifact.LifecycleStatus != ArtifactLifecycleStatus.Current)
        {
            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Inconsistent,
                SessionId: sessionId,
                CurrentArtifactId: currentArtifactId,
                ExpectedArtifactId: currentArtifactId,
                Reason: $"Current artifact '{currentArtifactId}' is not marked as IsCurrent = true (Status: {artifact.LifecycleStatus})."
            );
        }

        if (artifact.VisualRevision != sessionState.VisualRevision)
        {
            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Corrupted,
                SessionId: sessionId,
                CurrentArtifactId: currentArtifactId,
                ExpectedArtifactId: null,
                Reason: $"VisualRevision mismatch: Artifact revision {artifact.VisualRevision} vs SessionState revision {sessionState.VisualRevision}."
            );
        }

        // 5. Lineage check against authoritative attempt if job is referenced
        if (sessionState.CurrentGenerationJobId.HasValue)
        {
            var job = await _dbContext.ImageGenerationJobs
                .AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == sessionState.CurrentGenerationJobId.Value, ct);

            if (job != null && job.AcceptedAttemptId.HasValue)
            {
                var attempt = await _dbContext.ImageGenerationAttempts
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == job.AcceptedAttemptId.Value, ct);

                if (attempt != null && attempt.AcceptedArtifactId.HasValue && attempt.AcceptedArtifactId.Value != artifact.Id)
                {
                    return new ArtifactConsistencyResult(
                        Status: VisualStateConsistencyStatus.Corrupted,
                        SessionId: sessionId,
                        CurrentArtifactId: currentArtifactId,
                        ExpectedArtifactId: attempt.AcceptedArtifactId.Value,
                        Reason: $"Job accepted attempt points to artifact '{attempt.AcceptedArtifactId.Value}' but session state points to '{artifact.Id}'."
                    );
                }
            }
        }

        // 6. Perfect lineage alignment
        return new ArtifactConsistencyResult(
            Status: VisualStateConsistencyStatus.Healthy,
            SessionId: sessionId,
            CurrentArtifactId: currentArtifactId,
            ExpectedArtifactId: currentArtifactId,
            Reason: "Visual session state and lineage are fully consistent."
        );
    }

    public async Task<ArtifactConsistencyResult> RepairVisualStateAsync(Guid sessionId, CancellationToken ct = default)
    {
        var diagnosis = await ValidateConsistencyAsync(sessionId, ct);

        if (diagnosis.Status == VisualStateConsistencyStatus.Healthy)
        {
            return diagnosis;
        }

        if (diagnosis.Status == VisualStateConsistencyStatus.Repairable && diagnosis.ExpectedArtifactId.HasValue)
        {
            var targetArtifactId = diagnosis.ExpectedArtifactId.Value;
            var targetArtifact = await _dbContext.SceneImages
                .FirstOrDefaultAsync(img => img.Id == targetArtifactId && img.SessionId == sessionId, ct);

            if (targetArtifact == null)
            {
                throw new InvalidOperationException($"Cannot repair session {sessionId}: Expected artifact '{targetArtifactId}' not found.");
            }

            // Demote other current artifacts
            var otherCurrent = await _dbContext.SceneImages
                .Where(img => img.SessionId == sessionId && img.IsCurrent && img.Id != targetArtifactId)
                .ToListAsync(ct);

            foreach (var img in otherCurrent)
            {
                img.DemoteCurrent();
            }

            targetArtifact.SetCurrent(true);

            var sessionState = await _dbContext.VisualSessionStates
                .FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);

            var now = DateTime.UtcNow;
            if (sessionState != null)
            {
                sessionState.RestoreCurrent(targetArtifact.Id, targetArtifact.GenerationJobId ?? Guid.Empty, targetArtifact.VisualRevision, now);
            }
            else
            {
                sessionState = new VisualSessionState(sessionId, targetArtifact.Id, targetArtifact.GenerationJobId, targetArtifact.VisualRevision, now);
                await _dbContext.VisualSessionStates.AddAsync(sessionState, ct);
            }

            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation("[VisualStateConsistencyService] Deterministically repaired VisualSessionState for SessionId={SessionId} to ArtifactId={ArtifactId}",
                sessionId, targetArtifact.Id);

            return new ArtifactConsistencyResult(
                Status: VisualStateConsistencyStatus.Healthy,
                SessionId: sessionId,
                CurrentArtifactId: targetArtifact.Id,
                ExpectedArtifactId: targetArtifact.Id,
                Reason: "Visual state was deterministically repaired from authoritative attempt ledger."
            );
        }

        if (diagnosis.Status == VisualStateConsistencyStatus.Inconsistent && diagnosis.CurrentArtifactId.HasValue)
        {
            var targetArtifactId = diagnosis.CurrentArtifactId.Value;
            var targetArtifact = await _dbContext.SceneImages
                .FirstOrDefaultAsync(img => img.Id == targetArtifactId && img.SessionId == sessionId, ct);

            if (targetArtifact != null && targetArtifact.LifecycleStatus != ArtifactLifecycleStatus.Deleted && targetArtifact.LifecycleStatus != ArtifactLifecycleStatus.Quarantined)
            {
                var otherCurrent = await _dbContext.SceneImages
                    .Where(img => img.SessionId == sessionId && img.IsCurrent && img.Id != targetArtifactId)
                    .ToListAsync(ct);

                foreach (var img in otherCurrent)
                {
                    img.DemoteCurrent();
                }

                targetArtifact.SetCurrent(true);
                await _dbContext.SaveChangesAsync(ct);

                return new ArtifactConsistencyResult(
                    Status: VisualStateConsistencyStatus.Healthy,
                    SessionId: sessionId,
                    CurrentArtifactId: targetArtifact.Id,
                    ExpectedArtifactId: targetArtifact.Id,
                    Reason: "Current artifact flag was repaired."
                );
            }
        }

        throw new InvalidOperationException($"Cannot repair visual state for session '{sessionId}': State is {diagnosis.Status} ({diagnosis.Reason}). Explicit manual reconciliation required.");
    }
}
