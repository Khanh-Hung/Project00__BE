using Application.Abstractions.Auth;
using Application.Features.Chat.Queries.VisualSession;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualSession;

public sealed class VisualAuthorizationTests
{
    private sealed class FakeCurrentUserProvider : ICurrentUserProvider
    {
        public string? CurrentUserId { get; set; }
        public string? Username => "testuser";
        public string? Email => "test@project00.ai";
    }

    private static (ProjectDbContext Db, Guid OwnerUserId, Guid ForeignUserId, Guid SessionId, Guid TurnId) SetupContext()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new ProjectDbContext(options);
        var ownerUserId = Guid.NewGuid();
        var foreignUserId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        // Create ChatSession owned by ownerUserId
        var session = new ChatSession(characterId, ownerUserId, "Test Session") { Id = sessionId };
        db.ChatSessions.Add(session);

        var job = new ImageGenerationJob(sessionId, turnId, characterId, 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);

        var attempt = new ImageGenerationAttempt(job.Id, turnId, 1, 1, 1000L, "{}", "fp-auth-setup", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");
        job.AcceptAttempt(attempt.Id, DateTime.UtcNow, "worker-1", "{}");

        // Create SceneImage
        var artifact = new SceneImage(
            sessionId: sessionId,
            characterId: characterId,
            turnId: turnId,
            sceneRevision: 1,
            imageUrl: "https://cdn.project00.ai/scene.png",
            prompt: "1girl",
            generationJobId: job.Id,
            generationFingerprint: "fp-auth-setup",
            visualRevision: 1,
            isCurrent: true,
            lifecycleStatus: ArtifactLifecycleStatus.Current
        );
        db.SceneImages.Add(artifact);

        var state = new VisualSessionState(sessionId, artifact.Id, job.Id, visualRevision: 1);
        db.VisualSessionStates.Add(state);

        db.ImageGenerationJobs.Add(job);
        db.ImageGenerationAttempts.Add(attempt);

        db.SaveChanges();

        return (db, ownerUserId, foreignUserId, sessionId, turnId);
    }

    [Fact]
    public async Task GetCurrentVisualState_EnforcesStrictAuthorization()
    {
        var (db, ownerId, foreignId, sessionId, _) = SetupContext();

        // 1. Owner -> 200 OK
        var authOwner = new FakeCurrentUserProvider { CurrentUserId = ownerId.ToString() };
        var handlerOwner = new GetCurrentVisualStateHandler(db, authOwner, NullLogger<GetCurrentVisualStateHandler>.Instance);
        var resOwner = await handlerOwner.Handle(new GetCurrentVisualStateQuery(sessionId), CancellationToken.None);
        Assert.True(resOwner.IsSuccess);
        Assert.Equal("https://cdn.project00.ai/scene.png", resOwner.Value!.ImageUrl);

        // 2. Foreign User -> 403 Forbidden
        var authForeign = new FakeCurrentUserProvider { CurrentUserId = foreignId.ToString() };
        var handlerForeign = new GetCurrentVisualStateHandler(db, authForeign, NullLogger<GetCurrentVisualStateHandler>.Instance);
        var resForeign = await handlerForeign.Handle(new GetCurrentVisualStateQuery(sessionId), CancellationToken.None);
        Assert.False(resForeign.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, resForeign.StatusCode);

        // 3. Unknown Session -> 404 NotFound
        var resUnknown = await handlerOwner.Handle(new GetCurrentVisualStateQuery(Guid.NewGuid()), CancellationToken.None);
        Assert.False(resUnknown.IsSuccess);
        Assert.Equal(StatusCodes.Status404NotFound, resUnknown.StatusCode);

        // 4. Unauthenticated -> 401 Unauthorized
        var authUnauth = new FakeCurrentUserProvider { CurrentUserId = null };
        var handlerUnauth = new GetCurrentVisualStateHandler(db, authUnauth, NullLogger<GetCurrentVisualStateHandler>.Instance);
        var resUnauth = await handlerUnauth.Handle(new GetCurrentVisualStateQuery(sessionId), CancellationToken.None);
        Assert.False(resUnauth.IsSuccess);
        Assert.Equal(StatusCodes.Status401Unauthorized, resUnauth.StatusCode);
    }

    [Fact]
    public async Task GetSessionVisualHistory_EnforcesStrictAuthorization()
    {
        var (db, ownerId, foreignId, sessionId, _) = SetupContext();
        var historyService = new VisualHistoryService(db, NullLogger<VisualHistoryService>.Instance);

        // 1. Owner -> 200 OK
        var authOwner = new FakeCurrentUserProvider { CurrentUserId = ownerId.ToString() };
        var handlerOwner = new GetSessionVisualHistoryHandler(db, authOwner, historyService);
        var resOwner = await handlerOwner.Handle(new GetSessionVisualHistoryQuery(sessionId), CancellationToken.None);
        Assert.True(resOwner.IsSuccess);
        Assert.Single(resOwner.Value!);

        // 2. Foreign User -> 403 Forbidden
        var authForeign = new FakeCurrentUserProvider { CurrentUserId = foreignId.ToString() };
        var handlerForeign = new GetSessionVisualHistoryHandler(db, authForeign, historyService);
        var resForeign = await handlerForeign.Handle(new GetSessionVisualHistoryQuery(sessionId), CancellationToken.None);
        Assert.False(resForeign.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, resForeign.StatusCode);

        // 3. Unknown Session -> 404 NotFound
        var resUnknown = await handlerOwner.Handle(new GetSessionVisualHistoryQuery(Guid.NewGuid()), CancellationToken.None);
        Assert.False(resUnknown.IsSuccess);
        Assert.Equal(StatusCodes.Status404NotFound, resUnknown.StatusCode);

        // 4. Unauthenticated -> 401 Unauthorized
        var authUnauth = new FakeCurrentUserProvider { CurrentUserId = null };
        var handlerUnauth = new GetSessionVisualHistoryHandler(db, authUnauth, historyService);
        var resUnauth = await handlerUnauth.Handle(new GetSessionVisualHistoryQuery(sessionId), CancellationToken.None);
        Assert.False(resUnauth.IsSuccess);
        Assert.Equal(StatusCodes.Status401Unauthorized, resUnauth.StatusCode);
    }

    [Fact]
    public async Task GetTurnImageStatus_EnforcesStrictAuthorization()
    {
        var (db, ownerId, foreignId, sessionId, turnId) = SetupContext();

        // 1. Owner -> 200 OK
        var authOwner = new FakeCurrentUserProvider { CurrentUserId = ownerId.ToString() };
        var handlerOwner = new GetTurnImageGenerationStatusHandler(db, authOwner);
        var resOwner = await handlerOwner.Handle(new GetTurnImageGenerationStatusQuery(sessionId, turnId), CancellationToken.None);
        Assert.True(resOwner.IsSuccess);

        // 2. Foreign User -> 403 Forbidden
        var authForeign = new FakeCurrentUserProvider { CurrentUserId = foreignId.ToString() };
        var handlerForeign = new GetTurnImageGenerationStatusHandler(db, authForeign);
        var resForeign = await handlerForeign.Handle(new GetTurnImageGenerationStatusQuery(sessionId, turnId), CancellationToken.None);
        Assert.False(resForeign.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, resForeign.StatusCode);

        // 3. Unknown Session -> 404 NotFound
        var resUnknown = await handlerOwner.Handle(new GetTurnImageGenerationStatusQuery(Guid.NewGuid(), turnId), CancellationToken.None);
        Assert.False(resUnknown.IsSuccess);
        Assert.Equal(StatusCodes.Status404NotFound, resUnknown.StatusCode);

        // 4. Unauthenticated -> 401 Unauthorized
        var authUnauth = new FakeCurrentUserProvider { CurrentUserId = null };
        var handlerUnauth = new GetTurnImageGenerationStatusHandler(db, authUnauth);
        var resUnauth = await handlerUnauth.Handle(new GetTurnImageGenerationStatusQuery(sessionId, turnId), CancellationToken.None);
        Assert.False(resUnauth.IsSuccess);
        Assert.Equal(StatusCodes.Status401Unauthorized, resUnauth.StatusCode);
    }

    [Fact]
    public async Task GetTurnImageStatus_ResolvesArtifactOfAcceptedAttempt_WhenMultipleAttemptsExist()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new ProjectDbContext(options);
        var userId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var session = new ChatSession(charId, userId, "Session") { Id = sessionId };
        db.ChatSessions.Add(session);

        var job = new ImageGenerationJob(sessionId, turnId, charId, 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);

        // Attempt 1: Failed / degraded
        var attempt1 = new ImageGenerationAttempt(job.Id, turnId, 1, 1, 1000L, "{}", "fp-attempt-1", GenerationAttemptStatus.Degraded, claimedBy: "worker-1");
        var artifact1 = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/attempt1.png", "prompt 1", generationJobId: job.Id, generationFingerprint: "fp-attempt-1", isCurrent: false, lifecycleStatus: ArtifactLifecycleStatus.Historical);

        // Attempt 2: Winning / Succeeded attempt
        var attempt2 = new ImageGenerationAttempt(job.Id, turnId, 1, 2, 2000L, "{}", "fp-attempt-2", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");
        var artifact2 = new SceneImage(sessionId, charId, turnId, 1, "https://cdn.project00.ai/attempt2_winning.png", "prompt 2", generationJobId: job.Id, generationFingerprint: "fp-attempt-2", isCurrent: true, lifecycleStatus: ArtifactLifecycleStatus.Current);

        job.AcceptAttempt(attempt2.Id, DateTime.UtcNow, "worker-1", "{}");

        db.ImageGenerationJobs.Add(job);
        db.ImageGenerationAttempts.AddRange(attempt1, attempt2);
        db.SceneImages.AddRange(artifact1, artifact2);
        await db.SaveChangesAsync();

        var authProvider = new FakeCurrentUserProvider { CurrentUserId = userId.ToString() };
        var handler = new GetTurnImageGenerationStatusHandler(db, authProvider);

        var result = await handler.Handle(new GetTurnImageGenerationStatusQuery(sessionId, turnId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.HasArtifact);
        // Must resolve artifact2 corresponding directly to attempt2 (AcceptedAttemptId)!
        Assert.Equal("https://cdn.project00.ai/attempt2_winning.png", result.Value.ImageUrl);
    }

    [Fact]
    public async Task GetTurnImageStatus_WhenAcceptedAttemptIdMissingInDb_Returns500StateDivergence()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new ProjectDbContext(options);
        var userId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var session = new ChatSession(charId, userId, "Session") { Id = sessionId };
        db.ChatSessions.Add(session);

        var missingAttemptId = Guid.NewGuid();
        var job = new ImageGenerationJob(sessionId, turnId, charId, 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);
        job.AcceptAttempt(missingAttemptId, DateTime.UtcNow, "worker-1", "{}");

        db.ImageGenerationJobs.Add(job);
        await db.SaveChangesAsync();

        var authProvider = new FakeCurrentUserProvider { CurrentUserId = userId.ToString() };
        var handler = new GetTurnImageGenerationStatusHandler(db, authProvider);

        var result = await handler.Handle(new GetTurnImageGenerationStatusQuery(sessionId, turnId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.Contains("State divergence", result.Errors.First());
    }

    [Fact]
    public async Task GetTurnImageStatus_WhenWinningArtifactMissingInDb_Returns500StateDivergence()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new ProjectDbContext(options);
        var userId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var session = new ChatSession(charId, userId, "Session") { Id = sessionId };
        db.ChatSessions.Add(session);

        var job = new ImageGenerationJob(sessionId, turnId, charId, 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);

        var winningAttempt = new ImageGenerationAttempt(job.Id, turnId, 1, 1, 1000L, "{}", "fp-winning-missing-art", GenerationAttemptStatus.Succeeded, claimedBy: "worker-1");
        job.AcceptAttempt(winningAttempt.Id, DateTime.UtcNow, "worker-1", "{}");

        db.ImageGenerationJobs.Add(job);
        db.ImageGenerationAttempts.Add(winningAttempt);
        // Note: SceneImages is intentionally empty to simulate artifact divergence / corruption
        await db.SaveChangesAsync();

        var authProvider = new FakeCurrentUserProvider { CurrentUserId = userId.ToString() };
        var handler = new GetTurnImageGenerationStatusHandler(db, authProvider);

        var result = await handler.Handle(new GetTurnImageGenerationStatusQuery(sessionId, turnId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.Contains("State divergence", result.Errors.First());
    }
}
