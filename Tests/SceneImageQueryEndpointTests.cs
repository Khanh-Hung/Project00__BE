using System.Text.Json;
using Application.Abstractions.Auth;
using Application.Common;
using Application.DTOs;
using Application.Features.Chat.Queries.GetSceneImageStatus;
using Application.Features.Chat.Queries.GetTurnSceneImages;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Project.Tests;

public sealed class SceneImageQueryEndpointTests
{
    private sealed class FakeUserProvider : ICurrentUserProvider
    {
        public string? CurrentUserId { get; set; }
        public string? CurrentUserEmail => "test@example.com";
        public string? CurrentUserRole => "User";
        public bool IsAuthenticated => !string.IsNullOrEmpty(CurrentUserId);

        public FakeUserProvider(string? userId = null)
        {
            CurrentUserId = userId;
        }
    }

    private static (CoreDbContext db, UnitOfWork uow, FakeUserProvider userProvider, GetSceneImageStatusHandler statusHandler, GetTurnSceneImagesHandler turnImagesHandler) CreateHarness(
        string dbName,
        string? currentUserId = null)
    {
        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        var db = new CoreDbContext(options);
        var uow = new UnitOfWork(db);
        var userProvider = new FakeUserProvider(currentUserId);
        var statusHandler = new GetSceneImageStatusHandler(
            uow,
            userProvider,
            NullLogger<GetSceneImageStatusHandler>.Instance
        );
        var turnImagesHandler = new GetTurnSceneImagesHandler(
            uow,
            userProvider
        );

        return (db, uow, userProvider, statusHandler, turnImagesHandler);
    }

    private static VisualSnapshot CreateTestSnapshot(Guid sessionId, Guid turnId, Guid characterId)
    {
        var profile = GenerationProfile.CreateDefault(
            workflow: "VisualIdentity",
            workflowVersion: 1,
            parametersJson: "{\"ipAdapter\":{\"weight\":0.45,\"endAt\":0.70}}"
        );

        return VisualSnapshot.Create(
            turnId: turnId,
            sessionId: sessionId,
            characterId: characterId,
            sceneRevision: 1,
            visualIdentity: new CharacterVisualIdentity(
                Face: "Delicate face",
                Hair: "Silver hair",
                Eyes: "Blue eyes",
                Skin: "Fair skin",
                Body: "Slender",
                AgeAppearance: "20s",
                ClothingStyle: "White Dress",
                Accessories: null,
                VisualTraits: null,
                CanonicalReferenceUrl: "https://cloud.storage/elysia_canonical.png"
            ),
            sceneState: new SessionSceneState(
                CurrentLocation: "Garden",
                CurrentPosition: "Altar",
                CurrentOutfit: "White Dress",
                CurrentTimeOfDay: "Morning",
                HeldItems: null,
                Atmosphere: "Peaceful",
                SceneRevision: 1,
                LastUpdatedAt: DateTime.UtcNow
            ),
            transientState: new TransientVisualState(
                Action: "Standing gracefully in garden",
                Pose: "Gentle posture",
                Expression: "Shy flustered smile"
            ),
            generationProfile: profile
        );
    }

    [Fact]
    public async Task GetSceneImageStatus_WhenCompleted_ReturnsImageUrlAndMetadata()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, statusHandler, _) = CreateHarness(dbName, userId.ToString());

        var session = new ChatSession(charId, userId, "Session 1") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);

        var sceneImage = new SceneImage(
            sessionId: sessionId,
            characterId: charId,
            turnId: turnId,
            sceneRevision: 1,
            imageUrl: "https://storage.cdn/scene_final.png",
            prompt: "masterpiece, 1girl, white dress in morning garden",
            generationRequestId: requestId,
            isCurrent: true
        );
        await db.SceneImages.AddAsync(sceneImage);
        await db.SaveChangesAsync();

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(requestId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("completed", result.Value.Status);
        Assert.Equal("https://storage.cdn/scene_final.png", result.Value.ImageUrl);
        Assert.Equal(requestId, result.Value.GenerationRequestId);
        Assert.Equal(turnId, result.Value.TurnId);
        Assert.Equal(sessionId, result.Value.SessionId);
        Assert.Equal(1, result.Value.SceneRevision);
    }

    [Fact]
    public async Task GetSceneImageStatus_WhenProcessing_ReturnsProcessingStatus()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, statusHandler, _) = CreateHarness(dbName, userId.ToString());

        var session = new ChatSession(charId, userId, "Session 1") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);

        var job = new ImageGenerationJob(sessionId, turnId, charId, 1, generationRequestId: requestId);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);
        job.SetProviderJobId("comfy-job-123");
        await db.ImageGenerationJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(requestId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("processing", result.Value.Status);
        Assert.Equal(requestId, result.Value.GenerationRequestId);
        Assert.Equal(turnId, result.Value.TurnId);
        Assert.Equal(sessionId, result.Value.SessionId);
    }

    [Fact]
    public async Task GetSceneImageStatus_WhenPending_ReturnsPendingStatus()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, statusHandler, _) = CreateHarness(dbName, userId.ToString());

        var session = new ChatSession(charId, userId, "Session 1") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);

        var job = new ImageGenerationJob(sessionId, turnId, charId, 1, generationRequestId: requestId);
        await db.ImageGenerationJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(requestId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("pending", result.Value.Status);
    }

    [Fact]
    public async Task GetSceneImageStatus_WhenQueuedInOutbox_ReturnsQueuedStatus()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, statusHandler, _) = CreateHarness(dbName, userId.ToString());

        var session = new ChatSession(charId, userId, "Session 1") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);

        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId,
            userId: userId,
            characterId: charId,
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Hello",
            assistantReply: "Hi",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger"
        );
        await db.CharacterTurns.AddAsync(turn);

        var snapshot = CreateTestSnapshot(sessionId, turnId, charId);
        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: charId,
            UserId: userId,
            Snapshot: snapshot,
            GenerationRequestId: requestId
        );

        var outboxMessage = new OutboxMessage(
            eventType: OutboxEventTypes.SceneImageGeneration,
            payloadJson: JsonSerializer.Serialize(payload)
        );
        await db.OutboxMessages.AddAsync(outboxMessage);
        await db.SaveChangesAsync();

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(requestId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("queued", result.Value.Status);
        Assert.Equal(requestId, result.Value.GenerationRequestId);
        Assert.Equal(turnId, result.Value.TurnId);
        Assert.Equal(sessionId, result.Value.SessionId);
    }

    [Fact]
    public async Task GetSceneImageStatus_WhenFailed_ReturnsFailureReasonAndRetryableFlag()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, statusHandler, _) = CreateHarness(dbName, userId.ToString());

        var session = new ChatSession(charId, userId, "Session 1") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);

        var job = new ImageGenerationJob(sessionId, turnId, charId, 1, generationRequestId: requestId);
        job.TryClaim("worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);
        job.Fail("ComfyUI Connection Timeout", isRetryable: true, DateTime.UtcNow, "worker-1");
        await db.ImageGenerationJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(requestId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("failed", result.Value.Status);
        Assert.Equal("ComfyUI Connection Timeout", result.Value.FailureReason);
        Assert.True(result.Value.IsRetryable);
    }

    [Fact]
    public async Task GetSceneImageStatus_NonExistentRequest_Returns404()
    {
        var nonExistentRequestId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (_, _, _, statusHandler, _) = CreateHarness(dbName, userId.ToString());

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(nonExistentRequestId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
    }

    [Fact]
    public async Task GetSceneImageStatus_CrossUserAttempt_Returns403Forbidden()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var maliciousUserId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, statusHandler, _) = CreateHarness(dbName, maliciousUserId.ToString());

        var session = new ChatSession(charId, ownerUserId, "Session 1") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);

        var sceneImage = new SceneImage(
            sessionId: sessionId,
            characterId: charId,
            turnId: turnId,
            sceneRevision: 1,
            imageUrl: "https://storage.cdn/private.png",
            prompt: "private prompt",
            generationRequestId: requestId,
            isCurrent: true
        );
        await db.SceneImages.AddAsync(sceneImage);
        await db.SaveChangesAsync();

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(requestId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task GetSceneImageStatus_Unauthenticated_Returns401Unauthorized()
    {
        var requestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (_, _, _, statusHandler, _) = CreateHarness(dbName, currentUserId: null);

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(requestId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task GetSceneImageStatus_WhenSessionHasNullUserId_Returns403Forbidden_FailClosed()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, statusHandler, _) = CreateHarness(dbName, currentUserId.ToString());

        // Session without UserId (null)
        var session = new ChatSession(charId, null, "Guest Session") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);

        var sceneImage = new SceneImage(
            sessionId: sessionId,
            characterId: charId,
            turnId: turnId,
            sceneRevision: 1,
            imageUrl: "https://storage.cdn/image.png",
            prompt: "prompt",
            generationRequestId: requestId,
            isCurrent: true
        );
        await db.SceneImages.AddAsync(sceneImage);
        await db.SaveChangesAsync();

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(requestId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task GetSceneImageStatus_WhenSessionHasEmptyUserId_Returns403Forbidden_FailClosed()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, statusHandler, _) = CreateHarness(dbName, currentUserId.ToString());

        // Session with Guid.Empty
        var session = new ChatSession(charId, Guid.Empty, "Guest Session") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);

        var sceneImage = new SceneImage(
            sessionId: sessionId,
            characterId: charId,
            turnId: turnId,
            sceneRevision: 1,
            imageUrl: "https://storage.cdn/image.png",
            prompt: "prompt",
            generationRequestId: requestId,
            isCurrent: true
        );
        await db.SceneImages.AddAsync(sceneImage);
        await db.SaveChangesAsync();

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(requestId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task GetSceneImageStatus_WhenOutboxHasEmptyUserId_Returns403Forbidden_FailClosed()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, statusHandler, _) = CreateHarness(dbName, currentUserId.ToString());

        var session = new ChatSession(charId, currentUserId, "Session 1") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);

        var snapshot = CreateTestSnapshot(sessionId, turnId, charId);
        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: charId,
            UserId: Guid.Empty, // Empty UserId in payload
            Snapshot: snapshot,
            GenerationRequestId: requestId
        );

        var outboxMessage = new OutboxMessage(
            eventType: OutboxEventTypes.SceneImageGeneration,
            payloadJson: JsonSerializer.Serialize(payload)
        );
        await db.OutboxMessages.AddAsync(outboxMessage);
        await db.SaveChangesAsync();

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(requestId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task GetSceneImageStatus_WhenOutboxSessionOwnerMismatch_Returns403Forbidden()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var maliciousUserId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        // Harness is maliciousUserId
        var (db, _, _, statusHandler, _) = CreateHarness(dbName, maliciousUserId.ToString());

        // Session belongs to ownerUserId, NOT maliciousUserId
        var session = new ChatSession(charId, ownerUserId, "Owner Session") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);

        var snapshot = CreateTestSnapshot(sessionId, turnId, charId);
        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: charId,
            UserId: maliciousUserId, // Payload forged with maliciousUserId, but session is owned by ownerUserId
            Snapshot: snapshot,
            GenerationRequestId: requestId
        );

        var outboxMessage = new OutboxMessage(
            eventType: OutboxEventTypes.SceneImageGeneration,
            payloadJson: JsonSerializer.Serialize(payload)
        );
        await db.OutboxMessages.AddAsync(outboxMessage);
        await db.SaveChangesAsync();

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(requestId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task GetSceneImageStatus_WhenOutboxSessionDoesNotExist_Returns404NotFound()
    {
        var nonExistentSessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, statusHandler, _) = CreateHarness(dbName, currentUserId.ToString());

        var snapshot = CreateTestSnapshot(nonExistentSessionId, turnId, charId);
        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: charId,
            UserId: currentUserId,
            Snapshot: snapshot,
            GenerationRequestId: requestId
        );

        var outboxMessage = new OutboxMessage(
            eventType: OutboxEventTypes.SceneImageGeneration,
            payloadJson: JsonSerializer.Serialize(payload)
        );
        await db.OutboxMessages.AddAsync(outboxMessage);
        await db.SaveChangesAsync();

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(requestId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
    }

    [Fact]
    public async Task GetSceneImageStatus_WhenOutboxPayloadUserIdMismatchesSessionUserId_Returns403Forbidden()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, statusHandler, _) = CreateHarness(dbName, currentUserId.ToString());

        // Session belongs to currentUserId
        var session = new ChatSession(charId, currentUserId, "Session 1") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);

        var snapshot = CreateTestSnapshot(sessionId, turnId, charId);
        // Payload has otherUserId instead of session owner currentUserId
        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: charId,
            UserId: otherUserId,
            Snapshot: snapshot,
            GenerationRequestId: requestId
        );

        var outboxMessage = new OutboxMessage(
            eventType: OutboxEventTypes.SceneImageGeneration,
            payloadJson: JsonSerializer.Serialize(payload)
        );
        await db.OutboxMessages.AddAsync(outboxMessage);
        await db.SaveChangesAsync();

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(requestId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Contains("ownership mismatch", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSceneImageStatus_WhenManyOutboxMessagesExist_FiltersDirectlyWithoutScanningUnrelatedMessages()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();
        var targetRequestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, statusHandler, _) = CreateHarness(dbName, currentUserId.ToString());

        var session = new ChatSession(charId, currentUserId, "Target Session") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);

        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId,
            userId: currentUserId,
            characterId: charId,
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Hello",
            assistantReply: "Hi",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger"
        );
        await db.CharacterTurns.AddAsync(turn);

        // Add 50 unrelated outbox messages
        for (int i = 0; i < 50; i++)
        {
            var unrelatedSnapshot = CreateTestSnapshot(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var unrelatedPayload = new SceneImageGenerationOutboxPayload(
                TurnId: Guid.NewGuid(),
                CharacterId: Guid.NewGuid(),
                UserId: Guid.NewGuid(),
                Snapshot: unrelatedSnapshot,
                GenerationRequestId: Guid.NewGuid()
            );
            await db.OutboxMessages.AddAsync(new OutboxMessage(
                eventType: OutboxEventTypes.SceneImageGeneration,
                payloadJson: JsonSerializer.Serialize(unrelatedPayload)
            ));
        }

        // Add target message
        var targetSnapshot = CreateTestSnapshot(sessionId, turnId, charId);
        var targetPayload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: charId,
            UserId: currentUserId,
            Snapshot: targetSnapshot,
            GenerationRequestId: targetRequestId
        );
        await db.OutboxMessages.AddAsync(new OutboxMessage(
            eventType: OutboxEventTypes.SceneImageGeneration,
            payloadJson: JsonSerializer.Serialize(targetPayload)
        ));
        await db.SaveChangesAsync();

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(targetRequestId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("queued", result.Value.Status);
        Assert.Equal(targetRequestId, result.Value.GenerationRequestId);
    }

    [Fact]
    public async Task GetTurnSceneImages_ReturnsAllImagesForTurn_OrderedByCreatedAtDesc()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var reqId1 = Guid.NewGuid();
        var reqId2 = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, _, turnImagesHandler) = CreateHarness(dbName, userId.ToString());

        var session = new ChatSession(charId, userId, "Session 1") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);

        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId,
            userId: userId,
            characterId: charId,
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Hello",
            assistantReply: "Hi",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger"
        );
        await db.CharacterTurns.AddAsync(turn);

        var image1 = new SceneImage(
            sessionId: sessionId,
            characterId: charId,
            turnId: turnId,
            sceneRevision: 1,
            imageUrl: "https://storage.cdn/image1.png",
            prompt: "prompt 1",
            generationRequestId: reqId1,
            isCurrent: false
        );
        var image2 = new SceneImage(
            sessionId: sessionId,
            characterId: charId,
            turnId: turnId,
            sceneRevision: 1,
            imageUrl: "https://storage.cdn/image2.png",
            prompt: "prompt 2",
            generationRequestId: reqId2,
            isCurrent: true
        );
        await db.SceneImages.AddRangeAsync(image1, image2);
        await db.SaveChangesAsync();

        var result = await turnImagesHandler.Handle(new GetTurnSceneImagesQuery(sessionId, turnId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value.Count);
        Assert.Equal(turnId, result.Value[0].TurnId);
        Assert.Equal(turnId, result.Value[1].TurnId);

        // One image is current, one is historic
        Assert.Contains(result.Value, img => img.IsCurrent && img.ImageUrl == "https://storage.cdn/image2.png");
        Assert.Contains(result.Value, img => !img.IsCurrent && img.ImageUrl == "https://storage.cdn/image1.png");
    }

    [Fact]
    public async Task GetTurnSceneImages_CrossUserAttempt_Returns403Forbidden()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var maliciousUserId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, _, turnImagesHandler) = CreateHarness(dbName, maliciousUserId.ToString());

        var session = new ChatSession(charId, ownerUserId, "Session 1") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);

        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId,
            userId: ownerUserId,
            characterId: charId,
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Hello",
            assistantReply: "Hi",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger"
        );
        await db.CharacterTurns.AddAsync(turn);
        await db.SaveChangesAsync();

        var result = await turnImagesHandler.Handle(new GetTurnSceneImagesQuery(sessionId, turnId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task GetTurnSceneImages_WhenSessionHasNullOrEmptyUserId_Returns403Forbidden_FailClosed()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, _, turnImagesHandler) = CreateHarness(dbName, currentUserId.ToString());

        var session = new ChatSession(charId, null, "Guest Session") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);

        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId,
            userId: currentUserId,
            characterId: charId,
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Hello",
            assistantReply: "Hi",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger"
        );
        await db.CharacterTurns.AddAsync(turn);
        await db.SaveChangesAsync();

        var result = await turnImagesHandler.Handle(new GetTurnSceneImagesQuery(sessionId, turnId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task GetTurnSceneImages_WrongSession_Returns404NotFound()
    {
        var nonExistentSessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (_, _, _, _, turnImagesHandler) = CreateHarness(dbName, userId.ToString());

        var result = await turnImagesHandler.Handle(new GetTurnSceneImagesQuery(nonExistentSessionId, turnId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
    }

    [Fact]
    public async Task GetTurnSceneImages_WhenTurnDoesNotExistInSession_Returns404NotFound()
    {
        var sessionId = Guid.NewGuid();
        var nonExistentTurnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, _, turnImagesHandler) = CreateHarness(dbName, userId.ToString());

        var session = new ChatSession(charId, userId, "Session 1") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);
        await db.SaveChangesAsync();

        var result = await turnImagesHandler.Handle(new GetTurnSceneImagesQuery(sessionId, nonExistentTurnId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
    }

    [Fact]
    public async Task GetTurnSceneImages_Unauthenticated_Returns401Unauthorized()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (_, _, _, _, turnImagesHandler) = CreateHarness(dbName, currentUserId: null);

        var result = await turnImagesHandler.Handle(new GetTurnSceneImagesQuery(sessionId, turnId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task GetSceneImageStatus_WhenOutboxSnapshotSessionIdMismatchesSessionId_Returns500InternalServerError()
    {
        var sessionId = Guid.NewGuid();
        var mismatchSessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, statusHandler, _) = CreateHarness(dbName, currentUserId.ToString());

        var session1 = new ChatSession(charId, currentUserId, "Session 1") { Id = sessionId };
        var session2 = new ChatSession(charId, currentUserId, "Session 2") { Id = mismatchSessionId };
        await db.ChatSessions.AddRangeAsync(session1, session2);

        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId,
            userId: currentUserId,
            characterId: charId,
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Hello",
            assistantReply: "Hi",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger"
        );
        await db.CharacterTurns.AddAsync(turn);

        // Snapshot has mismatchSessionId, but turn is in sessionId
        var snapshotWithWrongSessionId = CreateTestSnapshot(mismatchSessionId, turnId, charId);
        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: charId,
            UserId: currentUserId,
            Snapshot: snapshotWithWrongSessionId,
            GenerationRequestId: requestId
        );

        var outboxMessage = new OutboxMessage(
            eventType: OutboxEventTypes.SceneImageGeneration,
            payloadJson: JsonSerializer.Serialize(payload)
        );
        await db.OutboxMessages.AddAsync(outboxMessage);
        await db.SaveChangesAsync();

        // Query status
        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(requestId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.Contains("mismatch", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSceneImageStatus_WhenOutboxSnapshotTurnIdMismatchesPayloadTurnId_Returns500InternalServerError()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var mismatchTurnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, statusHandler, _) = CreateHarness(dbName, currentUserId.ToString());

        var session = new ChatSession(charId, currentUserId, "Session 1") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);

        // Snapshot has mismatchTurnId, but payload has turnId
        var snapshotWithWrongTurnId = CreateTestSnapshot(sessionId, mismatchTurnId, charId);
        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: charId,
            UserId: currentUserId,
            Snapshot: snapshotWithWrongTurnId,
            GenerationRequestId: requestId
        );

        var outboxMessage = new OutboxMessage(
            eventType: OutboxEventTypes.SceneImageGeneration,
            payloadJson: JsonSerializer.Serialize(payload)
        );
        await db.OutboxMessages.AddAsync(outboxMessage);
        await db.SaveChangesAsync();

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(requestId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.Contains("identity mismatch", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSceneImageStatus_WhenOutboxSnapshotCharacterIdMismatchesPayloadCharacterId_Returns500InternalServerError()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var mismatchCharId = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, statusHandler, _) = CreateHarness(dbName, currentUserId.ToString());

        var session = new ChatSession(charId, currentUserId, "Session 1") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);

        // Snapshot has mismatchCharId, but payload has charId
        var snapshotWithWrongCharId = CreateTestSnapshot(sessionId, turnId, mismatchCharId);
        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: charId,
            UserId: currentUserId,
            Snapshot: snapshotWithWrongCharId,
            GenerationRequestId: requestId
        );

        var outboxMessage = new OutboxMessage(
            eventType: OutboxEventTypes.SceneImageGeneration,
            payloadJson: JsonSerializer.Serialize(payload)
        );
        await db.OutboxMessages.AddAsync(outboxMessage);
        await db.SaveChangesAsync();

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(requestId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.Contains("identity mismatch", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSceneImageStatus_WhenOutboxCharacterIdMismatchesSessionCharacterId_Returns500InternalServerError()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var sessionCharId = Guid.NewGuid();
        var otherCharId = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, statusHandler, _) = CreateHarness(dbName, currentUserId.ToString());

        // Session has sessionCharId
        var session = new ChatSession(sessionCharId, currentUserId, "Session 1") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);

        // Payload has otherCharId
        var snapshot = CreateTestSnapshot(sessionId, turnId, otherCharId);
        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: otherCharId,
            UserId: currentUserId,
            Snapshot: snapshot,
            GenerationRequestId: requestId
        );

        var outboxMessage = new OutboxMessage(
            eventType: OutboxEventTypes.SceneImageGeneration,
            payloadJson: JsonSerializer.Serialize(payload)
        );
        await db.OutboxMessages.AddAsync(outboxMessage);
        await db.SaveChangesAsync();

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(requestId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.Contains("identity mismatch", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSceneImageStatus_WhenOutboxTurnDoesNotExist_Returns500InternalServerError()
    {
        var sessionId = Guid.NewGuid();
        var nonExistentTurnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, statusHandler, _) = CreateHarness(dbName, currentUserId.ToString());

        var session = new ChatSession(charId, currentUserId, "Session 1") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);
        await db.SaveChangesAsync();

        // Snapshot and payload point to nonExistentTurnId
        var snapshot = CreateTestSnapshot(sessionId, nonExistentTurnId, charId);
        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: nonExistentTurnId,
            CharacterId: charId,
            UserId: currentUserId,
            Snapshot: snapshot,
            GenerationRequestId: requestId
        );

        var outboxMessage = new OutboxMessage(
            eventType: OutboxEventTypes.SceneImageGeneration,
            payloadJson: JsonSerializer.Serialize(payload)
        );
        await db.OutboxMessages.AddAsync(outboxMessage);
        await db.SaveChangesAsync();

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(requestId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.Contains("turn for this queued generation request was not found", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSceneImageStatus_WhenOutboxTurnSessionIdMismatches_Returns500InternalServerError()
    {
        var sessionId1 = Guid.NewGuid();
        var sessionId2 = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, statusHandler, _) = CreateHarness(dbName, currentUserId.ToString());

        var session1 = new ChatSession(charId, currentUserId, "Session 1") { Id = sessionId1 };
        var session2 = new ChatSession(charId, currentUserId, "Session 2") { Id = sessionId2 };
        await db.ChatSessions.AddRangeAsync(session1, session2);

        // Turn is in session 1
        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId1,
            userId: currentUserId,
            characterId: charId,
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Hello",
            assistantReply: "Hi",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger"
        );
        await db.CharacterTurns.AddAsync(turn);

        // Outbox payload is targeting session 2
        var snapshot = CreateTestSnapshot(sessionId2, turnId, charId);
        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: charId,
            UserId: currentUserId,
            Snapshot: snapshot,
            GenerationRequestId: requestId
        );

        var outboxMessage = new OutboxMessage(
            eventType: OutboxEventTypes.SceneImageGeneration,
            payloadJson: JsonSerializer.Serialize(payload)
        );
        await db.OutboxMessages.AddAsync(outboxMessage);
        await db.SaveChangesAsync();

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(requestId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.Contains("metadata mismatch", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSceneImageStatus_WhenOutboxTurnCharacterIdMismatches_Returns500InternalServerError()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId1 = Guid.NewGuid();
        var charId2 = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, statusHandler, _) = CreateHarness(dbName, currentUserId.ToString());

        var session = new ChatSession(charId1, currentUserId, "Session 1") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);

        // Turn has charId2 instead of charId1
        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId,
            userId: currentUserId,
            characterId: charId2,
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Hello",
            assistantReply: "Hi",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger"
        );
        await db.CharacterTurns.AddAsync(turn);

        // Outbox payload has charId1
        var snapshot = CreateTestSnapshot(sessionId, turnId, charId1);
        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: charId1,
            UserId: currentUserId,
            Snapshot: snapshot,
            GenerationRequestId: requestId
        );

        var outboxMessage = new OutboxMessage(
            eventType: OutboxEventTypes.SceneImageGeneration,
            payloadJson: JsonSerializer.Serialize(payload)
        );
        await db.OutboxMessages.AddAsync(outboxMessage);
        await db.SaveChangesAsync();

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(requestId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.Contains("metadata mismatch", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSceneImageStatus_WhenOutboxTurnUserIdMismatches_Returns500InternalServerError()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, statusHandler, _) = CreateHarness(dbName, currentUserId.ToString());

        var session = new ChatSession(charId, currentUserId, "Session 1") { Id = sessionId };
        await db.ChatSessions.AddAsync(session);

        // Turn has otherUserId
        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId,
            userId: otherUserId,
            characterId: charId,
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Hello",
            assistantReply: "Hi",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger"
        );
        await db.CharacterTurns.AddAsync(turn);

        var snapshot = CreateTestSnapshot(sessionId, turnId, charId);
        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: charId,
            UserId: currentUserId,
            Snapshot: snapshot,
            GenerationRequestId: requestId
        );

        var outboxMessage = new OutboxMessage(
            eventType: OutboxEventTypes.SceneImageGeneration,
            payloadJson: JsonSerializer.Serialize(payload)
        );
        await db.OutboxMessages.AddAsync(outboxMessage);
        await db.SaveChangesAsync();

        var result = await statusHandler.Handle(new GetSceneImageStatusQuery(requestId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.Contains("metadata mismatch", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }
}
