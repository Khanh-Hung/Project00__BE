using System.Reflection;
using System.Text.Json;
using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Features.Chat.Commands.TriggerTurnSceneImage;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Project.Tests;

public sealed class UserTriggeredTurnImageGenerationTests
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

    private static (ProjectDbContext db, UnitOfWork uow, FakeUserProvider userProvider, TriggerTurnSceneImageGenerationHandler handler) CreateHarness(
        string dbName,
        string? currentUserId = null)
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        var db = new ProjectDbContext(options);
        var uow = new UnitOfWork(db);
        var userProvider = new FakeUserProvider(currentUserId);
        var handler = new TriggerTurnSceneImageGenerationHandler(
            uow,
            userProvider,
            NullLogger<TriggerTurnSceneImageGenerationHandler>.Instance
        );

        return (db, uow, userProvider, handler);
    }

    private static VisualSnapshot CreateTestSnapshot(Guid sessionId, Guid turnId, string outfit = "White Dress", int revision = 1)
    {
        var profile = GenerationProfile.CreateDefault(
            workflow: "VisualIdentity",
            workflowVersion: 1,
            parametersJson: "{\"ipAdapter\":{\"weight\":0.45,\"endAt\":0.70}}"
        );

        return VisualSnapshot.Create(
            turnId: turnId,
            sessionId: sessionId,
            characterId: Guid.NewGuid(),
            sceneRevision: revision,
            visualIdentity: new CharacterVisualIdentity(
                Face: "Delicate face",
                Hair: "Silver hair",
                Eyes: "Blue eyes",
                Skin: "Fair skin",
                Body: "Slender",
                AgeAppearance: "20s",
                ClothingStyle: outfit,
                Accessories: null,
                VisualTraits: null,
                CanonicalReferenceUrl: "https://cloud.storage/elysia_canonical.png"
            ),
            sceneState: new SessionSceneState(
                CurrentLocation: "Garden",
                CurrentPosition: "Altar",
                CurrentOutfit: outfit,
                CurrentTimeOfDay: "Morning",
                HeldItems: null,
                Atmosphere: "Peaceful",
                SceneRevision: revision,
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
    public async Task Test1_GenerateImageFromTurn_UsesFrozenSnapshot()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, uow, _, handler) = CreateHarness(dbName, userId.ToString());

        var frozenSnapshot = CreateTestSnapshot(sessionId, turnId, outfit: "White Dress");
        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId,
            userId: userId,
            characterId: frozenSnapshot.CharacterId,
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Hello",
            assistantReply: "Greetings",
            mood: "Happy",
            moodIntensity: 50,
            affectionDelta: 1,
            affectionScore: 10,
            relationshipStage: "Friend",
            visualSnapshotJson: JsonSerializer.Serialize(frozenSnapshot)
        );

        await db.CharacterTurns.AddAsync(turn);
        await db.SaveChangesAsync();

        // Trigger scene image generation
        var result = await handler.Handle(new TriggerTurnSceneImageGenerationCommand(sessionId, turnId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(StatusCodes.Status202Accepted, result.StatusCode);
        Assert.NotNull(result.Value);

        // Verify outbox message contains the exact frozen snapshot
        var outboxMessage = await db.OutboxMessages.FirstOrDefaultAsync(m => m.EventType == OutboxEventTypes.SceneImageGeneration);
        Assert.NotNull(outboxMessage);

        var payload = JsonSerializer.Deserialize<SceneImageGenerationOutboxPayload>(outboxMessage.PayloadJson);
        Assert.NotNull(payload);
        Assert.Equal(frozenSnapshot.SceneState.CurrentOutfit, payload.Snapshot.SceneState.CurrentOutfit);
        Assert.Equal(frozenSnapshot.VisualIdentity?.ClothingStyle, payload.Snapshot.VisualIdentity?.ClothingStyle);
        Assert.Equal(frozenSnapshot.GenerationProfile.Workflow, payload.Snapshot.GenerationProfile.Workflow);
    }

    [Fact]
    public async Task Test2_GenerateImageFromTurn_DoesNotReadCurrentCharacterState_ZeroStateDrift()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, uow, _, handler) = CreateHarness(dbName, userId.ToString());

        // 1. Turn 10 frozen snapshot: Outfit = White Dress
        var frozenTurn10Snapshot = CreateTestSnapshot(sessionId, turnId, outfit: "White Dress");

        var turn10 = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId,
            userId: userId,
            characterId: charId,
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Look at my dress",
            assistantReply: "It is white and beautiful",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 10,
            relationshipStage: "Friend",
            visualSnapshotJson: JsonSerializer.Serialize(frozenTurn10Snapshot)
        );
        await db.CharacterTurns.AddAsync(turn10);

        // 2. Character entity state has since mutated to Black Armor in current database
        var currentCharacter = new Character(
            name: "Elysia",
            title: "Knight",
            avatarUrl: "https://cloud.storage/elysia_armor.png",
            personalityPrompt: "Armored warrior",
            greeting: "Ready for battle",
            category: "Anime",
            worldDescription: "Dark Fantasy",
            visualIdentity: new CharacterVisualIdentity(
                Face: "Battle scarred",
                Hair: "Crimson red hair",
                Eyes: "Golden eyes",
                Skin: "Pale",
                Body: "Muscular",
                AgeAppearance: "30s",
                ClothingStyle: "Heavy Black Armor",
                Accessories: "Broadsword",
                VisualTraits: null,
                CanonicalReferenceUrl: "https://cloud.storage/elysia_armor.png"
            )
        ) { Id = charId };
        await db.Characters.AddAsync(currentCharacter);
        await db.SaveChangesAsync();

        // 3. User triggers Generate Image on Turn 10
        var result = await handler.Handle(new TriggerTurnSceneImageGenerationCommand(sessionId, turnId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(StatusCodes.Status202Accepted, result.StatusCode);

        // 4. INVARIANT: Outbox Payload MUST use Turn 10's White Dress, NOT current Character's Black Armor!
        var outboxMessage = await db.OutboxMessages.FirstOrDefaultAsync(m => m.EventType == OutboxEventTypes.SceneImageGeneration);
        Assert.NotNull(outboxMessage);

        var payload = JsonSerializer.Deserialize<SceneImageGenerationOutboxPayload>(outboxMessage.PayloadJson);
        Assert.NotNull(payload);
        Assert.Equal("White Dress", payload.Snapshot.SceneState.CurrentOutfit);
        Assert.Equal("White Dress", payload.Snapshot.VisualIdentity?.ClothingStyle);
        Assert.NotEqual("Heavy Black Armor", payload.Snapshot.VisualIdentity?.ClothingStyle);
        Assert.Null(payload.Snapshot.VisualIdentity?.Accessories);
    }

    [Fact]
    public async Task Test3_GenerateImageFromTurn_CreatesNewGenerationRequest()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, handler) = CreateHarness(dbName, userId.ToString());

        var frozenSnapshot = CreateTestSnapshot(sessionId, turnId, outfit: "Mage Robes");
        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId,
            userId: userId,
            characterId: frozenSnapshot.CharacterId,
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Hello",
            assistantReply: "Hi",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger",
            visualSnapshotJson: JsonSerializer.Serialize(frozenSnapshot)
        );
        await db.CharacterTurns.AddAsync(turn);
        await db.SaveChangesAsync();

        var result = await handler.Handle(new TriggerTurnSceneImageGenerationCommand(sessionId, turnId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotEqual(Guid.Empty, result.Value!.GenerationRequestId);
        Assert.Equal(turnId, result.Value.TurnId);
        Assert.Equal("queued", result.Value.Status);
    }

    [Fact]
    public async Task Test4_GenerateImageFromTurn_IsAsyncAndCreatesOutbox()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, handler) = CreateHarness(dbName, userId.ToString());

        var frozenSnapshot = CreateTestSnapshot(sessionId, turnId);
        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId,
            userId: userId,
            characterId: frozenSnapshot.CharacterId,
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Hello",
            assistantReply: "Hi",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger",
            visualSnapshotJson: JsonSerializer.Serialize(frozenSnapshot)
        );
        await db.CharacterTurns.AddAsync(turn);
        await db.SaveChangesAsync();

        var result = await handler.Handle(new TriggerTurnSceneImageGenerationCommand(sessionId, turnId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(StatusCodes.Status202Accepted, result.StatusCode);

        var outboxCount = await db.OutboxMessages.CountAsync(m => m.EventType == OutboxEventTypes.SceneImageGeneration);
        Assert.Equal(1, outboxCount);
    }

    [Fact]
    public async Task Test5_RegenerateSameTurn_CreatesDifferentGenerationRequest()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, handler) = CreateHarness(dbName, userId.ToString());

        var frozenSnapshot = CreateTestSnapshot(sessionId, turnId);
        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId,
            userId: userId,
            characterId: frozenSnapshot.CharacterId,
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Hello",
            assistantReply: "Hi",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger",
            visualSnapshotJson: JsonSerializer.Serialize(frozenSnapshot)
        );
        await db.CharacterTurns.AddAsync(turn);
        await db.SaveChangesAsync();

        // First Generation
        var result1 = await handler.Handle(new TriggerTurnSceneImageGenerationCommand(sessionId, turnId), CancellationToken.None);
        Assert.True(result1.IsSuccess);
        var req1 = result1.Value!.GenerationRequestId;

        // Second Generation (Regenerate on the same turn)
        var result2 = await handler.Handle(new TriggerTurnSceneImageGenerationCommand(sessionId, turnId), CancellationToken.None);
        Assert.True(result2.IsSuccess);
        var req2 = result2.Value!.GenerationRequestId;

        // Both belong to the same TurnId, but have distinct GenerationRequestIds
        Assert.Equal(turnId, result1.Value.TurnId);
        Assert.Equal(turnId, result2.Value.TurnId);
        Assert.NotEqual(req1, req2);

        var outboxMessages = await db.OutboxMessages.Where(m => m.EventType == OutboxEventTypes.SceneImageGeneration).ToListAsync();
        Assert.Equal(2, outboxMessages.Count);

        var payload1 = JsonSerializer.Deserialize<SceneImageGenerationOutboxPayload>(outboxMessages[0].PayloadJson);
        var payload2 = JsonSerializer.Deserialize<SceneImageGenerationOutboxPayload>(outboxMessages[1].PayloadJson);
        Assert.Equal(req1, payload1!.GenerationRequestId);
        Assert.Equal(req2, payload2!.GenerationRequestId);
    }

    [Fact]
    public async Task Test6_MissingTurn_Returns404()
    {
        var sessionId = Guid.NewGuid();
        var nonExistentTurnId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (_, _, _, handler) = CreateHarness(dbName, userId.ToString());

        var result = await handler.Handle(new TriggerTurnSceneImageGenerationCommand(sessionId, nonExistentTurnId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
    }

    [Fact]
    public async Task Test7_SnapshotMissing_FailsClearly()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, handler) = CreateHarness(dbName, userId.ToString());

        // Turn without VisualSnapshotJson
        var turnWithoutSnapshot = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId,
            userId: userId,
            characterId: Guid.NewGuid(),
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Old conversation",
            assistantReply: "Before visual snapshot support",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger",
            visualSnapshotJson: null // Missing!
        );
        await db.CharacterTurns.AddAsync(turnWithoutSnapshot);
        await db.SaveChangesAsync();

        var result = await handler.Handle(new TriggerTurnSceneImageGenerationCommand(sessionId, turnId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Contains("Visual snapshot is not available", result.Errors[0]);
    }

    [Fact]
    public async Task Test8_GenerateImage_DoesNotCallLLM()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, handler) = CreateHarness(dbName, userId.ToString());

        var frozenSnapshot = CreateTestSnapshot(sessionId, turnId);
        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId,
            userId: userId,
            characterId: frozenSnapshot.CharacterId,
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Hello",
            assistantReply: "Hi",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger",
            visualSnapshotJson: JsonSerializer.Serialize(frozenSnapshot)
        );
        await db.CharacterTurns.AddAsync(turn);
        await db.SaveChangesAsync();

        // Handler does not inject ILLMService; execution succeeds solely using persisted snapshot
        var result = await handler.Handle(new TriggerTurnSceneImageGenerationCommand(sessionId, turnId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(StatusCodes.Status202Accepted, result.StatusCode);
    }

    [Fact]
    public void Test9_GenerateImage_DoesNotBlockOnComfyUI_ArchitecturalDecoupling()
    {
        // Architectural Invariant Test:
        // Verify that TriggerTurnSceneImageGenerationHandler has ZERO dependency on GPU/ComfyUI/LLM services.
        // It strictly coordinates with IUnitOfWork and ICurrentUserProvider to enqueue durable Outbox messages.
        var handlerType = typeof(TriggerTurnSceneImageGenerationHandler);
        var constructors = handlerType.GetConstructors();
        Assert.Single(constructors);

        var paramTypes = constructors[0].GetParameters().Select(p => p.ParameterType).ToList();

        // Assert required decoupled dependencies
        Assert.Contains(typeof(IUnitOfWork), paramTypes);
        Assert.Contains(typeof(ICurrentUserProvider), paramTypes);
        Assert.Contains(typeof(ILogger<TriggerTurnSceneImageGenerationHandler>), paramTypes);

        // Assert strictly NO heavy/downstream GPU or LLM dependencies
        Assert.DoesNotContain(paramTypes, t => t.Name.Contains("ComfyUI", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paramTypes, t => t.Name.Contains("ImageGenerationService", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paramTypes, t => t.Name.Contains("LLMService", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paramTypes, t => t.Name.Contains("VisualStateResolver", StringComparison.OrdinalIgnoreCase));

        // Assert handler fields also contain no prohibited services
        var fieldTypes = handlerType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Select(f => f.FieldType).ToList();
        Assert.DoesNotContain(fieldTypes, t => t.Name.Contains("ComfyUI", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fieldTypes, t => t.Name.Contains("ImageGenerationService", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fieldTypes, t => t.Name.Contains("LLMService", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Test10_Authorization_UserOwnsTurn_Succeeds202()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, handler) = CreateHarness(dbName, userId.ToString());

        var frozenSnapshot = CreateTestSnapshot(sessionId, turnId);
        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId,
            userId: userId,
            characterId: frozenSnapshot.CharacterId,
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Hello",
            assistantReply: "Hi",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger",
            visualSnapshotJson: JsonSerializer.Serialize(frozenSnapshot)
        );
        await db.CharacterTurns.AddAsync(turn);
        await db.SaveChangesAsync();

        var result = await handler.Handle(new TriggerTurnSceneImageGenerationCommand(sessionId, turnId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(StatusCodes.Status202Accepted, result.StatusCode);
    }

    [Fact]
    public async Task Test11_Authorization_CrossUser_Returns403Forbidden()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var maliciousUserId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        // Harness is configured with maliciousUserId as current authenticated user
        var (db, _, _, handler) = CreateHarness(dbName, maliciousUserId.ToString());

        var frozenSnapshot = CreateTestSnapshot(sessionId, turnId);
        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId,
            userId: ownerUserId, // Turn belongs to ownerUserId, NOT maliciousUserId
            characterId: frozenSnapshot.CharacterId,
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Private chat",
            assistantReply: "Private reply",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger",
            visualSnapshotJson: JsonSerializer.Serialize(frozenSnapshot)
        );
        await db.CharacterTurns.AddAsync(turn);
        await db.SaveChangesAsync();

        var result = await handler.Handle(new TriggerTurnSceneImageGenerationCommand(sessionId, turnId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Contains("permission", result.Errors[0], StringComparison.OrdinalIgnoreCase);

        // Invariant: No outbox message created on unauthorized trigger
        var outboxCount = await db.OutboxMessages.CountAsync();
        Assert.Equal(0, outboxCount);
    }

    [Fact]
    public async Task Test12_Authorization_EmptyLegacyUserId_Returns403Forbidden_NoImplicitBypass()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, handler) = CreateHarness(dbName, currentUserId.ToString());

        var frozenSnapshot = CreateTestSnapshot(sessionId, turnId);
        var turnWithEmptyUser = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId,
            userId: Guid.Empty, // Legacy / guest unassigned turn
            characterId: frozenSnapshot.CharacterId,
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Guest message",
            assistantReply: "Guest reply",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger",
            visualSnapshotJson: JsonSerializer.Serialize(frozenSnapshot)
        );
        await db.CharacterTurns.AddAsync(turnWithEmptyUser);
        await db.SaveChangesAsync();

        var result = await handler.Handle(new TriggerTurnSceneImageGenerationCommand(sessionId, turnId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);

        // Invariant: Must NOT allow implicit bypass for Guid.Empty turns
        var outboxCount = await db.OutboxMessages.CountAsync();
        Assert.Equal(0, outboxCount);
    }

    [Fact]
    public async Task Test13_Authorization_UnauthenticatedUser_Returns401Unauthorized()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        // Harness without authenticated user
        var (_, _, _, handler) = CreateHarness(dbName, currentUserId: null);

        var result = await handler.Handle(new TriggerTurnSceneImageGenerationCommand(sessionId, turnId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task Test14_WrongSession_Returns404NotFound()
    {
        var correctSessionId = Guid.NewGuid();
        var wrongSessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, handler) = CreateHarness(dbName, userId.ToString());

        var frozenSnapshot = CreateTestSnapshot(correctSessionId, turnId);
        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: correctSessionId, // Turn is in correctSessionId
            userId: userId,
            characterId: frozenSnapshot.CharacterId,
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Message in session A",
            assistantReply: "Reply in session A",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger",
            visualSnapshotJson: JsonSerializer.Serialize(frozenSnapshot)
        );
        await db.CharacterTurns.AddAsync(turn);
        await db.SaveChangesAsync();

        // Querying with wrongSessionId must return 404
        var result = await handler.Handle(new TriggerTurnSceneImageGenerationCommand(wrongSessionId, turnId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
    }

    [Fact]
    public async Task Test15_SnapshotCorruption_Returns500InternalServerError()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var (db, _, _, handler) = CreateHarness(dbName, userId.ToString());

        // Corrupted JSON in VisualSnapshotJson
        var turnWithCorruptedSnapshot = new CharacterTurn(
            turnId: turnId,
            sessionId: sessionId,
            userId: userId,
            characterId: Guid.NewGuid(),
            userMessageId: Guid.NewGuid(),
            assistantMessageId: Guid.NewGuid(),
            userMessage: "Corrupted message",
            assistantReply: "Corrupted reply",
            mood: "Neutral",
            moodIntensity: 50,
            affectionDelta: 0,
            affectionScore: 0,
            relationshipStage: "Stranger",
            visualSnapshotJson: "{corrupted-invalid-json-data: true"
        );
        await db.CharacterTurns.AddAsync(turnWithCorruptedSnapshot);
        await db.SaveChangesAsync();

        var result = await handler.Handle(new TriggerTurnSceneImageGenerationCommand(sessionId, turnId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.Contains("Failed to read frozen visual snapshot", result.Errors[0]);
    }
}
