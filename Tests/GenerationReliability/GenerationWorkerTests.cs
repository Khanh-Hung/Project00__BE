using Application.DTOs;
using Application.Enums;
using Application.Exceptions;
using Application.Interfaces;
using Application.Services;
using Domain.Common.DateTimes;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.GenerationReliability;

public sealed class GenerationWorkerTests
{
    private sealed class TrackingOrchestrator : IImageGenerationOrchestrator
    {
        public SceneImageGenerationOutboxPayload? LastPayload { get; private set; }
        public Guid? LastOutboxId { get; private set; }
        private readonly Exception? _exceptionToThrow;

        public TrackingOrchestrator(Exception? exceptionToThrow = null) => _exceptionToThrow = exceptionToThrow;

        public Task<JobExecutionResult> OrchestrateSceneImageGenerationAsync(
            SceneImageGenerationOutboxPayload payload,
            Guid outboxId,
            string workerId,
            DateTime now,
            CancellationToken ct = default)
        {
            LastPayload = payload;
            LastOutboxId = outboxId;

            if (_exceptionToThrow != null)
                throw _exceptionToThrow;

            return Task.FromResult(new JobExecutionResult(JobExecutionStatus.Completed));
        }
    }

    private static GenerationWorkItem CreateTestWorkItem(Guid? userId = null, Guid? outboxId = null)
    {
        var snapshot = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            SceneRevision: 1,
            VisualIdentity: new CharacterVisualIdentity(Face: "canonical_face", CanonicalReferenceUrl: "https://cdn.project00.ai/face.png"),
            SceneState: new SessionSceneState("active scene", "neutral"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault()
        );

        var payload = new SceneImageGenerationOutboxPayload(
            TurnId: snapshot.TurnId,
            CharacterId: snapshot.CharacterId,
            UserId: userId ?? Guid.NewGuid(),
            Snapshot: snapshot,
            GenerationRequestId: Guid.NewGuid()
        );

        return new GenerationWorkItem(payload, outboxId ?? Guid.NewGuid(), DateTime.UtcNow, Priority: 5);
    }

    [Fact]
    public async Task Worker_HappyPath_InvokesOrchestrator_WithAuthoritativeIdentityAndUserId()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var dbInit = new CoreDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
        }

        var trackingOrchestrator = new TrackingOrchestrator();
        var services = new ServiceCollection();
        services.AddScoped(_ => new CoreDbContext(options));
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton(GenerationRetryPolicy.Default);
        services.AddScoped<IImageGenerationOrchestrator>(_ => trackingOrchestrator);

        var sp = services.BuildServiceProvider();
        using var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 10);
        var worker = new GenerationWorker(
            scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
            jobQueue: queue,
            logger: NullLogger<GenerationWorker>.Instance,
            workerId: "worker-test"
        );

        var expectedUserId = Guid.NewGuid();
        var expectedOutboxId = Guid.NewGuid();
        var item = CreateTestWorkItem(userId: expectedUserId, outboxId: expectedOutboxId);

        var result = await worker.ProcessWorkItemDirectAsync(item);
        Assert.Equal(JobExecutionStatus.Completed, result.Status);

        // Verify authoritative payload was preserved without bogus regeneration
        Assert.NotNull(trackingOrchestrator.LastPayload);
        Assert.Equal(expectedUserId, trackingOrchestrator.LastPayload.UserId);
        Assert.Equal(expectedOutboxId, trackingOrchestrator.LastOutboxId);
        Assert.NotNull(trackingOrchestrator.LastPayload.Snapshot.VisualIdentity);
        Assert.Equal("canonical_face", trackingOrchestrator.LastPayload.Snapshot.VisualIdentity.Face);
    }

    [Fact]
    public async Task Worker_TransientError_ReturnsDeferred()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var dbInit = new CoreDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => new CoreDbContext(options));
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton(GenerationRetryPolicy.Deterministic(maxRetries: 3, baseDelay: TimeSpan.FromSeconds(2)));
        services.AddScoped<IImageGenerationOrchestrator>(_ => new TrackingOrchestrator(new GpuTransientException("Timeout", 408)));

        var sp = services.BuildServiceProvider();
        using var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 10);
        var worker = new GenerationWorker(
            scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
            jobQueue: queue,
            logger: NullLogger<GenerationWorker>.Instance,
            workerId: "worker-test"
        );

        var item = CreateTestWorkItem();
        var result = await worker.ProcessWorkItemDirectAsync(item);
        Assert.Equal(JobExecutionStatus.Deferred, result.Status);
    }

    [Fact]
    public async Task Worker_PermanentError_ReturnsFailed()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var dbInit = new CoreDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => new CoreDbContext(options));
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton(GenerationRetryPolicy.Deterministic(maxRetries: 3));
        services.AddScoped<IImageGenerationOrchestrator>(_ => new TrackingOrchestrator(new GpuNonTransientException("Invalid node syntax", 400)));

        var sp = services.BuildServiceProvider();
        using var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 10);
        var worker = new GenerationWorker(
            scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
            jobQueue: queue,
            logger: NullLogger<GenerationWorker>.Instance,
            workerId: "worker-test"
        );

        var item = CreateTestWorkItem();
        var result = await worker.ProcessWorkItemDirectAsync(item);
        Assert.Equal(JobExecutionStatus.Failed, result.Status);
    }
}
