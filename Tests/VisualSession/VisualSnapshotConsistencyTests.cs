using System.Text.Json;
using Application.Abstractions.Auth;
using Application.Features.Chat.Commands.TriggerTurnSceneImage;
using Domain.Entities;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualSession;

public sealed class VisualSnapshotConsistencyTests
{
    private sealed class FakeCurrentUserProvider : ICurrentUserProvider
    {
        public string? CurrentUserId { get; set; }
        public string? Username => "testuser";
        public string? Email => "test@project00.ai";
    }

    [Fact]
    public async Task MismatchedSnapshotIdentity_RejectsGenerationWithServerError()
    {
        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new CoreDbContext(options);
        var unitOfWork = new UnitOfWork(db);
        var userId = Guid.NewGuid();
        var authProvider = new FakeCurrentUserProvider { CurrentUserId = userId.ToString() };
        var handler = new TriggerTurnSceneImageGenerationHandler(unitOfWork, authProvider, NullLogger<TriggerTurnSceneImageGenerationHandler>.Instance);

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var characterId = Guid.NewGuid();

        // Corrupted snapshot with different SessionId
        var corruptedSnapshot = new VisualSnapshot(
            TurnId: turnId,
            SessionId: Guid.NewGuid(), // Mismatched!
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
            visualSnapshotJson: JsonSerializer.Serialize(corruptedSnapshot)
        );
        await db.CharacterTurns.AddAsync(turn);
        await db.SaveChangesAsync();

        var command = new TriggerTurnSceneImageGenerationCommand(sessionId, turnId);
        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.Contains("identity does not match", result.Errors.First());
    }
}
