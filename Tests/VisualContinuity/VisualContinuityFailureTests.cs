using Application.Common.Exceptions;
using Application.DTOs;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualContinuity;

public sealed class VisualContinuityFailureTests
{
    private sealed class FaultyStateReader : ISceneVisualStateReader
    {
        public Task<SceneVisualState?> GetLatestBySessionAsync(Guid sessionId, CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated catastrophic DB storage disconnection.");
        }

        public Task<SceneVisualState?> GetLatestBySessionAndSceneKeyAsync(Guid sessionId, string sceneKey, CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated catastrophic DB storage disconnection.");
        }

        public Task<SceneVisualState?> GetLatestByCharacterIdAsync(Guid characterId, CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated catastrophic DB storage disconnection.");
        }

        public Task SaveStateAsync(SceneVisualState state, uint expectedVersion = 0, CancellationToken ct = default)
        {
            throw new InvalidOperationException("Simulated catastrophic DB storage disconnection.");
        }
    }

    private sealed class StubStateReader : ISceneVisualStateReader
    {
        public Task<SceneVisualState?> GetLatestBySessionAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult<SceneVisualState?>(null);
        public Task<SceneVisualState?> GetLatestBySessionAndSceneKeyAsync(Guid sessionId, string sceneKey, CancellationToken ct = default) => Task.FromResult<SceneVisualState?>(null);
        public Task<SceneVisualState?> GetLatestByCharacterIdAsync(Guid characterId, CancellationToken ct = default) => Task.FromResult<SceneVisualState?>(null);
        public Task SaveStateAsync(SceneVisualState state, uint expectedVersion = 0, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task ContinuityResolver_NullRequest_ThrowsArgumentNullException()
    {
        var resolver = new VisualContinuityResolver(new StubStateReader(), NullLogger<VisualContinuityResolver>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            resolver.ResolveAsync(null!));
    }

    [Fact]
    public async Task ContinuityResolver_NullIntent_ThrowsArgumentException()
    {
        var resolver = new VisualContinuityResolver(new StubStateReader(), NullLogger<VisualContinuityResolver>.Instance);
        var context = new SceneCompositionContext(Guid.NewGuid());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            resolver.ResolveAsync(new VisualContinuityRequest(null!, context)));
    }

    [Fact]
    public async Task ContinuityResolver_DatabaseFailure_ThrowsVisualContinuityResolutionException_FailFast()
    {
        // Arrange: Database reader throws an unexpected database exception
        var faultyReader = new FaultyStateReader();
        var resolver = new VisualContinuityResolver(faultyReader, NullLogger<VisualContinuityResolver>.Instance);

        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var intent = new SceneIntent(
            characterId: charId,
            locationHint: "Throne Room",
            actionHint: "sitting on throne",
            sessionId: sessionId,
            turnId: turnId
        );

        var context = new SceneCompositionContext(
            CharacterId: charId,
            SessionId: sessionId,
            TurnId: turnId,
            SceneRevision: 1
        );

        // Act & Assert: Throws typed VisualContinuityResolutionException and does NOT silently fallback
        var ex = await Assert.ThrowsAsync<VisualContinuityResolutionException>(() =>
            resolver.ResolveAsync(new VisualContinuityRequest(intent, context, 1)));

        Assert.Equal(SceneCompositionFailureCategory.ContextResolutionFailure, ex.FailureCategory);
        Assert.Equal(sessionId, ex.SessionId);
    }
}
