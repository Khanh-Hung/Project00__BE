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

        // Create SceneImage
        var artifact = new SceneImage(
            sessionId: sessionId,
            characterId: characterId,
            turnId: turnId,
            sceneRevision: 1,
            imageUrl: "https://cdn.project00.ai/scene.png",
            prompt: "1girl",
            visualRevision: 1,
            isCurrent: true,
            lifecycleStatus: ArtifactLifecycleStatus.Current
        );
        db.SceneImages.Add(artifact);

        var state = new VisualSessionState(sessionId, artifact.Id, Guid.NewGuid(), visualRevision: 1);
        db.VisualSessionStates.Add(state);

        var job = new ImageGenerationJob(sessionId, turnId, characterId, 1);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);
        job.AcceptAttempt(Guid.NewGuid(), DateTime.UtcNow, "worker-1", "{}");
        db.ImageGenerationJobs.Add(job);

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
}
