using System.Text.Json;
using Application.Abstractions.Auth;
using Application.Common;
using Application.DTOs;
using Application.Features.Chat.Commands.TriggerTurnSceneImage;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualSession;

public sealed class VisualIdempotencyTests
{
    private sealed class FakeCurrentUserProvider : ICurrentUserProvider
    {
        public string? CurrentUserId { get; set; }
        public string? Username => "testuser";
        public string? Email => "test@project00.ai";
    }

    [Fact]
    public async Task SameRequestId_CalledMultipleTimes_ReturnsExistingJobWithoutDuplicateOutbox()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new ProjectDbContext(options);
        var unitOfWork = new UnitOfWork(db);
        var userId = Guid.NewGuid();
        var authProvider = new FakeCurrentUserProvider { CurrentUserId = userId.ToString() };
        var handler = new TriggerTurnSceneImageGenerationHandler(unitOfWork, authProvider, NullLogger<TriggerTurnSceneImageGenerationHandler>.Instance);

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        var snapshot = new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: characterId,
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("courtyard", "standing"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: 1000L)
        );

        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId,
            userId: userId,
            characterId: characterId,
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Hello",
            assistantReply: "Roleplay",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger",
            visualSnapshotJson: JsonSerializer.Serialize(snapshot)
        );
        await db.CharacterTurns.AddAsync(turn);
        await db.SaveChangesAsync();

        var command = new TriggerTurnSceneImageGenerationCommand(sessionId, turnId, RequestId: requestId);

        // First call -> enqueues outbox message
        var result1 = await handler.Handle(command, CancellationToken.None);
        Assert.Equal(StatusCodes.Status202Accepted, result1.StatusCode);
        Assert.Equal(requestId, result1.Value!.GenerationRequestId);

        // Pre-create the job in DB as if worker picked it up
        var job = new ImageGenerationJob(sessionId, turnId, characterId, 1, requestId);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);
        await db.ImageGenerationJobs.AddAsync(job);
        await db.SaveChangesAsync();

        // Second call with same RequestId -> returns existing job without adding new outbox messages
        var result2 = await handler.Handle(command, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, result2.StatusCode);
        Assert.Equal(requestId, result2.Value!.GenerationRequestId);

        var outboxCount = await db.OutboxMessages.CountAsync(m => m.EventType == OutboxEventTypes.SceneImageGeneration);
        Assert.Equal(1, outboxCount); // Exactly 1 outbox message created
    }

    [Fact]
    public async Task InFlightJob_CollapsesDuplicateRequests()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new ProjectDbContext(options);
        var unitOfWork = new UnitOfWork(db);
        var userId = Guid.NewGuid();
        var authProvider = new FakeCurrentUserProvider { CurrentUserId = userId.ToString() };
        var handler = new TriggerTurnSceneImageGenerationHandler(unitOfWork, authProvider, NullLogger<TriggerTurnSceneImageGenerationHandler>.Instance);

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var existingRequestId = Guid.NewGuid();

        var snapshot = new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: characterId,
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("courtyard", "standing"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: 1000L)
        );

        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId,
            userId: userId,
            characterId: characterId,
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Hello",
            assistantReply: "Roleplay",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger",
            visualSnapshotJson: JsonSerializer.Serialize(snapshot)
        );
        await db.CharacterTurns.AddAsync(turn);

        // In-flight active job
        var activeJob = new ImageGenerationJob(sessionId, turnId, characterId, 1, existingRequestId);
        activeJob.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);
        await db.ImageGenerationJobs.AddAsync(activeJob);
        await db.SaveChangesAsync();

        // Call without explicit RequestId -> collapses onto existing in-flight job
        var command = new TriggerTurnSceneImageGenerationCommand(sessionId, turnId, RequestId: null);
        var result = await handler.Handle(command, CancellationToken.None);

        Assert.Equal(StatusCodes.Status202Accepted, result.StatusCode);
        Assert.Equal(existingRequestId, result.Value!.GenerationRequestId);

        // No new outbox message created
        var outboxCount = await db.OutboxMessages.CountAsync();
        Assert.Equal(0, outboxCount);
    }
}
