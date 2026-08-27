using System.Text.Json;
using Application.Abstractions.Auth;
using Application.Common;
using Application.DTOs;
using Application.Features.Chat.Commands.TriggerTurnSceneImage;
using Domain.Entities;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
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
    public async Task Concurrent50IdenticalRequests_YieldsExactlyOneJobAndOneOutboxMessage()
    {
        // 1. Setup SQLite in-memory database with persistent connection across threads
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var dbOptions = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connection)
            .Options;

        // Ensure database schema is created
        using (var setupDb = new ProjectDbContext(dbOptions))
        {
            await setupDb.Database.EnsureCreatedAsync();
        }

        var userId = Guid.NewGuid();
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

        // Seed initial Turn data
        using (var seedDb = new ProjectDbContext(dbOptions))
        {
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
            await seedDb.CharacterTurns.AddAsync(turn);
            await seedDb.SaveChangesAsync();
        }

        // 2. Launch 50 concurrent tasks with independent DbContext and UnitOfWork instances
        int concurrency = 50;
        var tasks = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            await using var db = new ProjectDbContext(dbOptions);
            var uow = new UnitOfWork(db);
            var auth = new FakeCurrentUserProvider { CurrentUserId = userId.ToString() };
            var handler = new TriggerTurnSceneImageGenerationHandler(uow, auth, NullLogger<TriggerTurnSceneImageGenerationHandler>.Instance);

            var command = new TriggerTurnSceneImageGenerationCommand(sessionId, turnId, RequestId: requestId);
            return await handler.Handle(command, CancellationToken.None);
        });

        var results = await Task.WhenAll(tasks);

        // 3. Verify all 50 requests completed successfully without throwing exceptions
        Assert.Equal(concurrency, results.Length);
        foreach (var result in results)
        {
            Assert.True(result.IsSuccess);
            Assert.Equal(requestId, result.Value!.GenerationRequestId);
        }

        // 4. Authoritative Database Assertions: EXACTLY 1 Job and EXACTLY 1 Outbox Message!
        using (var verifyDb = new ProjectDbContext(dbOptions))
        {
            var jobCount = await verifyDb.ImageGenerationJobs
                .CountAsync(j => j.SessionId == sessionId && j.GenerationRequestId == requestId);
            Assert.Equal(1, jobCount);

            var outboxCount = await verifyDb.OutboxMessages
                .CountAsync(m => m.EventType == OutboxEventTypes.SceneImageGeneration);
            Assert.Equal(1, outboxCount);
        }
    }
}
