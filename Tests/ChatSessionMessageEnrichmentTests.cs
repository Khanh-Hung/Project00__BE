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

    private sealed class FakeDateTimeProvider : IDateTimeProvider
    {
        public DateTime UtcNow { get; set; }

        public FakeDateTimeProvider(DateTime? utcNow = null)
        {
            UtcNow = utcNow ?? DateTime.UtcNow;
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
        var handler = new GetChatSessionHandler(uow, userProvider, new FakeDateTimeProvider());

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
        var handler = new GetChatSessionHandler(uow, userProvider, new FakeDateTimeProvider());

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
        var handler = new GetChatSessionHandler(uow, userProvider, new FakeDateTimeProvider());

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
        var handler = new GetChatSessionHandler(uow, userProvider, new FakeDateTimeProvider());

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
    public async Task GetChatSession_WhenTurnHasActiveAndFailedJobs_PrioritizesActiveJobOverFailedJob()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var db = new ProjectDbContext(options);
        var uow = new UnitOfWork(db);
        var userId = Guid.NewGuid();
        var userProvider = new FakeUserProvider(userId.ToString());
        var handler = new GetChatSessionHandler(uow, userProvider, new FakeDateTimeProvider());

        var character = new Character("Elysia", "Herrscher of Human", "avatar.png", "Flirty", "Default greeting", "Anime", new List<string>());
        await db.Characters.AddAsync(character);

        var session = new ChatSession(character.Id, userId, "Chat with Elysia");
        var userMsg = session.AddUserMessage("Hello");
        var assistantMsg = session.AddAssistantMessage("Welcome back!");
        await db.ChatSessions.AddAsync(session);

        var turnId = Guid.NewGuid();
        var genReqActive = Guid.NewGuid();
        var genReqFailed = Guid.NewGuid();

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

        // Job 1 (Active / Processing, created earlier with 5 min lease)
        var jobActive = new ImageGenerationJob(session.Id, turnId, character.Id, 1, generationRequestId: genReqActive);
        jobActive.MarkProcessing("comfy-1", "worker-1", TimeSpan.FromMinutes(5), DateTime.UtcNow.AddMinutes(-2));
        await db.ImageGenerationJobs.AddAsync(jobActive);

        // Job 2 (Failed, created later)
        var jobFailed = new ImageGenerationJob(session.Id, turnId, character.Id, 2, generationRequestId: genReqFailed);
        jobFailed.MarkFailed("cancelled", isRetryable: true);
        await db.ImageGenerationJobs.AddAsync(jobFailed);
        await db.SaveChangesAsync();

        var result = await handler.Handle(new GetChatSessionQuery(session.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var enrichedAssistant = result.Value!.Messages.First(m => m.Role == MessageRole.Assistant);
        Assert.Equal(turnId, enrichedAssistant.TurnId);
        // Active in-flight job takes precedence over failed job regardless of CreatedAt timestamp
        Assert.Equal("processing", enrichedAssistant.SceneImageStatus);
        Assert.Equal(genReqActive, enrichedAssistant.GenerationRequestId);
    }

    [Fact]
    public async Task GetChatSession_WhenTurnHasFailedAndProcessingJobs_PrioritizesProcessingJobOverFailedJob()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var db = new ProjectDbContext(options);
        var uow = new UnitOfWork(db);
        var userId = Guid.NewGuid();
        var userProvider = new FakeUserProvider(userId.ToString());
        var handler = new GetChatSessionHandler(uow, userProvider, new FakeDateTimeProvider());

        var character = new Character("Elysia", "Herrscher of Human", "avatar.png", "Flirty", "Default greeting", "Anime", new List<string>());
        await db.Characters.AddAsync(character);

        var session = new ChatSession(character.Id, userId, "Chat with Elysia");
        var userMsg = session.AddUserMessage("Hello");
        var assistantMsg = session.AddAssistantMessage("Welcome back!");
        await db.ChatSessions.AddAsync(session);

        var turnId = Guid.NewGuid();
        var genReqFailed = Guid.NewGuid();
        var genReqProcessing = Guid.NewGuid();

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

        // Job 1 (Failed, created earlier)
        var jobFailed = new ImageGenerationJob(session.Id, turnId, character.Id, 1, generationRequestId: genReqFailed);
        jobFailed.MarkFailed("Network error", isRetryable: true);
        await db.ImageGenerationJobs.AddAsync(jobFailed);

        // Job 2 (Processing, created later)
        var jobProcessing = new ImageGenerationJob(session.Id, turnId, character.Id, 2, generationRequestId: genReqProcessing);
        jobProcessing.MarkProcessing("comfy-2", "worker-1", TimeSpan.FromMinutes(2), DateTime.UtcNow);
        await db.ImageGenerationJobs.AddAsync(jobProcessing);
        await db.SaveChangesAsync();

        var result = await handler.Handle(new GetChatSessionQuery(session.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var enrichedAssistant = result.Value!.Messages.First(m => m.Role == MessageRole.Assistant);
        Assert.Equal(turnId, enrichedAssistant.TurnId);
        Assert.Equal("processing", enrichedAssistant.SceneImageStatus);
        Assert.Equal(genReqProcessing, enrichedAssistant.GenerationRequestId);
    }

    [Fact]
    public async Task GetChatSession_WhenTurnHasExpiredLeaseProcessingJobAndCurrentImage_PreservesCurrentImageAndMarksCompleted()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var db = new ProjectDbContext(options);
        var uow = new UnitOfWork(db);
        var userId = Guid.NewGuid();
        var userProvider = new FakeUserProvider(userId.ToString());
        var fixedTime = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
        var timeProvider = new FakeDateTimeProvider(fixedTime);
        var handler = new GetChatSessionHandler(uow, userProvider, timeProvider);

        var character = new Character("Elysia", "Herrscher of Human", "avatar.png", "Flirty", "Default greeting", "Anime", new List<string>());
        await db.Characters.AddAsync(character);

        var session = new ChatSession(character.Id, userId, "Chat with Elysia");
        var userMsg = session.AddUserMessage("Hello");
        var assistantMsg = session.AddAssistantMessage("Welcome back!");
        await db.ChatSessions.AddAsync(session);

        var turnId = Guid.NewGuid();
        var genReqImage1 = Guid.NewGuid();
        var genReqStaleJob = Guid.NewGuid();

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
            generationRequestId: genReqImage1,
            isCurrent: true
        );
        await db.SceneImages.AddAsync(sceneImage1);

        // Stale regeneration Job (Lease expired 5 minutes before fixedTime)
        var staleJob = new ImageGenerationJob(session.Id, turnId, character.Id, 2, generationRequestId: genReqStaleJob);
        staleJob.MarkProcessing("comfy-stale", "worker-dead", TimeSpan.FromMinutes(2), fixedTime.AddMinutes(-10));
        staleJob.ExpireLease(fixedTime.AddMinutes(-5));
        await db.ImageGenerationJobs.AddAsync(staleJob);
        await db.SaveChangesAsync();

        var result = await handler.Handle(new GetChatSessionQuery(session.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var enrichedAssistant = result.Value!.Messages.First(m => m.Role == MessageRole.Assistant);
        Assert.Equal(turnId, enrichedAssistant.TurnId);
        // Stale expired-lease job does not mask the completed current image
        Assert.Equal("completed", enrichedAssistant.SceneImageStatus);
        Assert.Equal("https://storage.cdn/elysia_rev1.png", enrichedAssistant.SceneImageUrl);
        Assert.Equal(genReqImage1, enrichedAssistant.GenerationRequestId);
    }

    [Fact]
    public async Task GetChatSession_WhenTurnHasExpiredLeaseProcessingJobAndNoCurrentImage_MarksFailed()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var db = new ProjectDbContext(options);
        var uow = new UnitOfWork(db);
        var userId = Guid.NewGuid();
        var userProvider = new FakeUserProvider(userId.ToString());
        var fixedTime = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
        var timeProvider = new FakeDateTimeProvider(fixedTime);
        var handler = new GetChatSessionHandler(uow, userProvider, timeProvider);

        var character = new Character("Elysia", "Herrscher of Human", "avatar.png", "Flirty", "Default greeting", "Anime", new List<string>());
        await db.Characters.AddAsync(character);

        var session = new ChatSession(character.Id, userId, "Chat with Elysia");
        var userMsg = session.AddUserMessage("Hello");
        var assistantMsg = session.AddAssistantMessage("Welcome back!");
        await db.ChatSessions.AddAsync(session);

        var turnId = Guid.NewGuid();
        var genReqStaleJob = Guid.NewGuid();

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

        // Stale Job with expired lease and no prior image
        var staleJob = new ImageGenerationJob(session.Id, turnId, character.Id, 1, generationRequestId: genReqStaleJob);
        staleJob.MarkProcessing("comfy-stale", "worker-dead", TimeSpan.FromMinutes(2), fixedTime.AddMinutes(-10));
        staleJob.ExpireLease(fixedTime.AddMinutes(-5));
        await db.ImageGenerationJobs.AddAsync(staleJob);
        await db.SaveChangesAsync();

        var result = await handler.Handle(new GetChatSessionQuery(session.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var enrichedAssistant = result.Value!.Messages.First(m => m.Role == MessageRole.Assistant);
        Assert.Equal(turnId, enrichedAssistant.TurnId);
        // Stale expired-lease job with no completed image is marked failed to enable retry
        Assert.Equal("failed", enrichedAssistant.SceneImageStatus);
        Assert.Equal(genReqStaleJob, enrichedAssistant.GenerationRequestId);
    }

    [Fact]
    public async Task GetChatSession_WhenTurnHasProcessingJobWithLeaseUntilEqualToNow_IsTreatedAsStaleAndMarksFailed()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var db = new ProjectDbContext(options);
        var uow = new UnitOfWork(db);
        var userId = Guid.NewGuid();
        var userProvider = new FakeUserProvider(userId.ToString());
        var fixedTime = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
        var timeProvider = new FakeDateTimeProvider(fixedTime);
        var handler = new GetChatSessionHandler(uow, userProvider, timeProvider);

        var character = new Character("Elysia", "Herrscher of Human", "avatar.png", "Flirty", "Default greeting", "Anime", new List<string>());
        await db.Characters.AddAsync(character);

        var session = new ChatSession(character.Id, userId, "Chat with Elysia");
        var userMsg = session.AddUserMessage("Hello");
        var assistantMsg = session.AddAssistantMessage("Welcome back!");
        await db.ChatSessions.AddAsync(session);

        var turnId = Guid.NewGuid();
        var genReqStaleJob = Guid.NewGuid();

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

        // Boundary test: Lease expired EXACTLY at fixedTime (LeaseUntil == now)
        var staleJob = new ImageGenerationJob(session.Id, turnId, character.Id, 1, generationRequestId: genReqStaleJob);
        staleJob.MarkProcessing("comfy-stale", "worker-dead", TimeSpan.FromMinutes(2), fixedTime.AddMinutes(-2));
        staleJob.ExpireLease(fixedTime);
        await db.ImageGenerationJobs.AddAsync(staleJob);
        await db.SaveChangesAsync();

        var result = await handler.Handle(new GetChatSessionQuery(session.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var enrichedAssistant = result.Value!.Messages.First(m => m.Role == MessageRole.Assistant);
        Assert.Equal(turnId, enrichedAssistant.TurnId);
        // Boundary: LeaseUntil == now is treated as expired/stale (not in-flight) and marks failed
        Assert.Equal("failed", enrichedAssistant.SceneImageStatus);
        Assert.Equal(genReqStaleJob, enrichedAssistant.GenerationRequestId);
    }

    [Fact]
    public async Task GetChatSession_WhenTurnHasCancelledJob_EnrichesMessageWithCancelledStatus()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var db = new ProjectDbContext(options);
        var uow = new UnitOfWork(db);
        var userId = Guid.NewGuid();
        var userProvider = new FakeUserProvider(userId.ToString());
        var handler = new GetChatSessionHandler(uow, userProvider, new FakeDateTimeProvider());

        var character = new Character("Elysia", "Herrscher of Human", "avatar.png", "Flirty", "Default greeting", "Anime", new List<string>());
        await db.Characters.AddAsync(character);

        var session = new ChatSession(character.Id, userId, "Chat with Elysia");
        var userMsg = session.AddUserMessage("Hello");
        var assistantMsg = session.AddAssistantMessage("Welcome back!");
        await db.ChatSessions.AddAsync(session);

        var turnId = Guid.NewGuid();
        var genReqCancelled = Guid.NewGuid();

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

        var cancelledJob = new ImageGenerationJob(session.Id, turnId, character.Id, 1, generationRequestId: genReqCancelled);
        cancelledJob.MarkCancelled();
        await db.ImageGenerationJobs.AddAsync(cancelledJob);
        await db.SaveChangesAsync();

        var result = await handler.Handle(new GetChatSessionQuery(session.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var enrichedAssistant = result.Value!.Messages.First(m => m.Role == MessageRole.Assistant);
        Assert.Equal(turnId, enrichedAssistant.TurnId);
        Assert.Equal("cancelled", enrichedAssistant.SceneImageStatus);
        Assert.Equal(genReqCancelled, enrichedAssistant.GenerationRequestId);
    }

    [Fact]
    public void FromJobStatus_WhenGivenUnknownStatus_ThrowsArgumentOutOfRangeException()
    {
        var invalidStatus = (ImageJobStatus)999;
        Assert.Throws<ArgumentOutOfRangeException>(() => SceneImageStatuses.FromJobStatus(invalidStatus));
    }
}
