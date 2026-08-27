using System.Text.Json;
using Application.Abstractions.Auth;
using Application.Common;
using Application.DTOs;
using Application.Features.Chat.Commands.RegenerateTurnSceneImage;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualSession;

public sealed class VisualRegenerationTests
{
    private sealed class FakeCurrentUserProvider : ICurrentUserProvider
    {
        public string? CurrentUserId { get; set; }
        public string? Username => "testuser";
        public string? Email => "test@project00.ai";
    }

    [Fact]
    public async Task RegenerateTurnImage_CreatesIndependentJob_WithAuthoritativePredecessor()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new ProjectDbContext(options);
        var unitOfWork = new UnitOfWork(db);
        var userId = Guid.NewGuid();
        var authProvider = new FakeCurrentUserProvider { CurrentUserId = userId.ToString() };
        var predecessorResolver = new VisualPredecessorResolver(db, NullLogger<VisualPredecessorResolver>.Instance);
        var handler = new RegenerateTurnSceneImageHandler(unitOfWork, authProvider, predecessorResolver, NullLogger<RegenerateTurnSceneImageHandler>.Instance);

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var characterId = Guid.NewGuid();

        // 1. Initial accepted artifact at VisualRevision 1
        var initialArtifact = new SceneImage(
            sessionId: sessionId,
            characterId: characterId,
            turnId: turnId,
            sceneRevision: 1,
            imageUrl: "https://cdn.project00.ai/initial_turn1.png",
            prompt: "1girl standing",
            visualRevision: 1,
            isCurrent: true,
            lifecycleStatus: ArtifactLifecycleStatus.Current
        );
        await db.SceneImages.AddAsync(initialArtifact);

        var sessionState = new VisualSessionState(sessionId, initialArtifact.Id, Guid.NewGuid(), visualRevision: 1);
        await db.VisualSessionStates.AddAsync(sessionState);

        // 2. CharacterTurn with frozen VisualSnapshot
        var snapshot = new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: characterId,
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("castle", "standing"),
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
            userMessage: "Hello traveler",
            assistantReply: "Roleplay text",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger",
            visualSnapshotJson: JsonSerializer.Serialize(snapshot)
        );
        await db.CharacterTurns.AddAsync(turn);
        await db.SaveChangesAsync();

        // 3. Trigger Regeneration Command
        var explicitRequestId = Guid.NewGuid();
        var command = new RegenerateTurnSceneImageCommand(sessionId, turnId, RequestId: explicitRequestId);
        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(StatusCodes.Status202Accepted, result.StatusCode);
        Assert.Equal(explicitRequestId, result.Value!.GenerationRequestId);

        // 4. Verify Outbox message was enqueued with authoritative predecessor resolved
        var outboxMsg = await db.OutboxMessages.FirstAsync(m => m.EventType == OutboxEventTypes.SceneImageGeneration);
        var payload = JsonSerializer.Deserialize<SceneImageGenerationOutboxPayload>(outboxMsg.PayloadJson);

        Assert.NotNull(payload);
        Assert.Equal(explicitRequestId, payload.GenerationRequestId);
        Assert.Equal(initialArtifact.ImageUrl, payload.Snapshot.PreviousSceneImageUrl);
        Assert.Equal(initialArtifact.Id, payload.Snapshot.PredecessorSceneImageId);
    }
}
