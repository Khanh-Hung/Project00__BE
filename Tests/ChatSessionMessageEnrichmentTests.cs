using Application.Abstractions.Auth;
using Application.Common;
using Application.DTOs;
using Application.Features.Chat.Commands.SendChatMessage;
using Application.Features.Chat.Queries.GetChatSession;
using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Project.Tests;

public sealed class ChatSessionMessageEnrichmentTests
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

    [Fact]
    public async Task GetChatSession_EnrichesAssistantMessagesWithTurnIdAndCompletedSceneImage()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var db = new ProjectDbContext(options);
        var uow = new UnitOfWork(db);
        var userId = Guid.NewGuid();
        var userProvider = new FakeUserProvider(userId.ToString());
        var handler = new GetChatSessionHandler(uow, userProvider);

        var character = new Character("Elysia", "Herrscher of Human", "avatar.png", "Flirty", "Default greeting", "Anime", new List<string>());
        await db.Characters.AddAsync(character);

        var session = new ChatSession(character.Id, userId, "Chat with Elysia");
        var userMsg = session.AddUserMessage("Hi Elysia!");
        var assistantMsg = session.AddAssistantMessage("Hello dear, missed me?");
        await db.ChatSessions.AddAsync(session);

        var turnId = Guid.NewGuid();
        var genReqId = Guid.NewGuid();

        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: session.Id,
            userId: userId,
            characterId: character.Id,
            userMessageId: userMsg.Id,
            assistantMessageId: assistantMsg.Id,
            userMessage: userMsg.Content,
            assistantReply: assistantMsg.Content,
            mood: "Joy",
            moodIntensity: 80,
            affectionDelta: 2,
            affectionScore: 10,
            relationshipStage: "Friend"
        );
        await db.CharacterTurns.AddAsync(turn);

        var sceneImage = new SceneImage(
            sessionId: session.Id,
            characterId: character.Id,
            turnId: turnId,
            sceneRevision: 1,
            imageUrl: "https://storage.cdn/elysia_completed.png",
            prompt: "1girl, smiling",
            generationRequestId: genReqId,
            isCurrent: true
        );
        await db.SceneImages.AddAsync(sceneImage);
        await db.SaveChangesAsync();

        var result = await handler.Handle(new GetChatSessionQuery(session.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value.Messages.Count);

        var enrichedAssistant = result.Value.Messages.First(m => m.Role == MessageRole.Assistant);
        Assert.Equal(turnId, enrichedAssistant.TurnId);
        Assert.Equal("https://storage.cdn/elysia_completed.png", enrichedAssistant.SceneImageUrl);
        Assert.Equal("completed", enrichedAssistant.SceneImageStatus);
        Assert.Equal(genReqId, enrichedAssistant.GenerationRequestId);
    }

    [Fact]
    public async Task GetChatSession_WhenJobProcessing_EnrichesMessageWithProcessingStatus()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var db = new ProjectDbContext(options);
        var uow = new UnitOfWork(db);
        var userId = Guid.NewGuid();
        var userProvider = new FakeUserProvider(userId.ToString());
        var handler = new GetChatSessionHandler(uow, userProvider);

        var character = new Character("Elysia", "Herrscher of Human", "avatar.png", "Flirty", "Default greeting", "Anime", new List<string>());
        await db.Characters.AddAsync(character);

        var session = new ChatSession(character.Id, userId, "Chat with Elysia");
        var userMsg = session.AddUserMessage("Let's go to the garden.");
        var assistantMsg = session.AddAssistantMessage("I'd love to accompany you!");
        await db.ChatSessions.AddAsync(session);

        var turnId = Guid.NewGuid();
        var genReqId = Guid.NewGuid();

        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: session.Id,
            userId: userId,
            characterId: character.Id,
            userMessageId: userMsg.Id,
            assistantMessageId: assistantMsg.Id,
            userMessage: userMsg.Content,
            assistantReply: assistantMsg.Content,
            mood: "Joy",
            moodIntensity: 80,
            affectionDelta: 2,
            affectionScore: 10,
            relationshipStage: "Friend"
        );
        await db.CharacterTurns.AddAsync(turn);

        var job = new ImageGenerationJob(session.Id, turnId, character.Id, 1, generationRequestId: genReqId);
        job.MarkProcessing("comfy-exec-1", "worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);
        await db.ImageGenerationJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var result = await handler.Handle(new GetChatSessionQuery(session.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var enrichedAssistant = result.Value!.Messages.First(m => m.Role == MessageRole.Assistant);
        Assert.Equal(turnId, enrichedAssistant.TurnId);
        Assert.Null(enrichedAssistant.SceneImageUrl);
        Assert.Equal("processing", enrichedAssistant.SceneImageStatus);
        Assert.Equal(genReqId, enrichedAssistant.GenerationRequestId);
    }

    [Fact]
    public async Task GetChatSession_WhenRegenerating_EnrichesMessageWithInFlightJobStatusAndPreviousImage()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var db = new ProjectDbContext(options);
        var uow = new UnitOfWork(db);
        var userId = Guid.NewGuid();
        var userProvider = new FakeUserProvider(userId.ToString());
        var handler = new GetChatSessionHandler(uow, userProvider);

        var character = new Character("Elysia", "Herrscher of Human", "avatar.png", "Flirty", "Default greeting", "Anime", new List<string>());
        await db.Characters.AddAsync(character);

        var session = new ChatSession(character.Id, userId, "Chat with Elysia");
        var userMsg = session.AddUserMessage("Let's go to the garden.");
        var assistantMsg = session.AddAssistantMessage("I'd love to accompany you!");
        await db.ChatSessions.AddAsync(session);

        var turnId = Guid.NewGuid();
        var genReqId1 = Guid.NewGuid();
        var genReqId2 = Guid.NewGuid();

        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: session.Id,
            userId: userId,
            characterId: character.Id,
            userMessageId: userMsg.Id,
            assistantMessageId: assistantMsg.Id,
            userMessage: userMsg.Content,
            assistantReply: assistantMsg.Content,
            mood: "Joy",
            moodIntensity: 80,
            affectionDelta: 2,
            affectionScore: 10,
            relationshipStage: "Friend"
        );
        await db.CharacterTurns.AddAsync(turn);

        // Previous completed image #1
        var sceneImage1 = new SceneImage(
            sessionId: session.Id,
            characterId: character.Id,
            turnId: turnId,
            sceneRevision: 1,
            imageUrl: "https://storage.cdn/elysia_rev1.png",
            prompt: "1girl, smiling",
            generationRequestId: genReqId1,
            isCurrent: true
        );
        await db.SceneImages.AddAsync(sceneImage1);

        // In-flight regeneration Job #2
        var job2 = new ImageGenerationJob(session.Id, turnId, character.Id, 2, generationRequestId: genReqId2);
        job2.MarkProcessing("comfy-exec-2", "worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);
        await db.ImageGenerationJobs.AddAsync(job2);
        await db.SaveChangesAsync();

        var result = await handler.Handle(new GetChatSessionQuery(session.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var enrichedAssistant = result.Value!.Messages.First(m => m.Role == MessageRole.Assistant);
        Assert.Equal(turnId, enrichedAssistant.TurnId);
        Assert.Equal("processing", enrichedAssistant.SceneImageStatus);
        Assert.Equal(genReqId2, enrichedAssistant.GenerationRequestId);
        Assert.Equal("https://storage.cdn/elysia_rev1.png", enrichedAssistant.SceneImageUrl);
    }

    [Fact]
    public async Task GetChatSession_WhenRegenerationJobFails_PreservesPreviousCurrentImage()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var db = new ProjectDbContext(options);
        var uow = new UnitOfWork(db);
        var userId = Guid.NewGuid();
        var userProvider = new FakeUserProvider(userId.ToString());
        var handler = new GetChatSessionHandler(uow, userProvider);

        var character = new Character("Elysia", "Herrscher of Human", "avatar.png", "Flirty", "Default greeting", "Anime", new List<string>());
        await db.Characters.AddAsync(character);

        var session = new ChatSession(character.Id, userId, "Chat with Elysia");
        var userMsg = session.AddUserMessage("Let's go to the garden.");
        var assistantMsg = session.AddAssistantMessage("I'd love to accompany you!");
        await db.ChatSessions.AddAsync(session);

        var turnId = Guid.NewGuid();
        var genReqId1 = Guid.NewGuid();
        var genReqId2 = Guid.NewGuid();

        var turn = new CharacterTurn(
            turnId: turnId,
            sessionId: session.Id,
            userId: userId,
            characterId: character.Id,
            userMessageId: userMsg.Id,
            assistantMessageId: assistantMsg.Id,
            userMessage: userMsg.Content,
            assistantReply: assistantMsg.Content,
            mood: "Joy",
            moodIntensity: 80,
            affectionDelta: 2,
            affectionScore: 10,
            relationshipStage: "Friend"
        );
        await db.CharacterTurns.AddAsync(turn);

        // Previous completed image #1
        var sceneImage1 = new SceneImage(
            sessionId: session.Id,
            characterId: character.Id,
            turnId: turnId,
            sceneRevision: 1,
            imageUrl: "https://storage.cdn/elysia_rev1.png",
            prompt: "1girl, smiling",
            generationRequestId: genReqId1,
            isCurrent: true
        );
        await db.SceneImages.AddAsync(sceneImage1);

        // Failed regeneration Job #2
        var job2 = new ImageGenerationJob(session.Id, turnId, character.Id, 2, generationRequestId: genReqId2);
        job2.MarkFailed("ComfyUI render timeout", isRetryable: true);
        await db.ImageGenerationJobs.AddAsync(job2);
        await db.SaveChangesAsync();

        var result = await handler.Handle(new GetChatSessionQuery(session.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var enrichedAssistant = result.Value!.Messages.First(m => m.Role == MessageRole.Assistant);
        Assert.Equal(turnId, enrichedAssistant.TurnId);
        // Previous image #1 is preserved intact
        Assert.Equal("https://storage.cdn/elysia_rev1.png", enrichedAssistant.SceneImageUrl);
        Assert.Equal("completed", enrichedAssistant.SceneImageStatus);
    }

    [Fact]
    public void FromJobStatus_WhenGivenUnknownStatus_ThrowsArgumentOutOfRangeException()
    {
        var invalidStatus = (ImageJobStatus)999;
        Assert.Throws<ArgumentOutOfRangeException>(() => SceneImageStatuses.FromJobStatus(invalidStatus));
    }
}
