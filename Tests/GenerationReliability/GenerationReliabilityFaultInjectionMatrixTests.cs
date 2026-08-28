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

public sealed class GenerationReliabilityFaultInjectionMatrixTests
{
    private sealed class ConfigurableOrchestrator : IImageGenerationOrchestrator
    {
        private readonly Func<SceneImageGenerationOutboxPayload, Task<JobExecutionResult>> _action;
        public ConfigurableOrchestrator(Func<SceneImageGenerationOutboxPayload, Task<JobExecutionResult>> action) => _action = action;

        public Task<JobExecutionResult> OrchestrateSceneImageGenerationAsync(
            SceneImageGenerationOutboxPayload payload,
            Guid outboxId,
            string workerId,
            DateTime now,
            CancellationToken ct = default)
        {
            return _action(payload);
        }
    }

    private static GenerationWorkItem CreateTestWorkItem(Guid? requestId = null, Guid? userId = null)
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
            GenerationRequestId: requestId ?? Guid.NewGuid()
        );

        return new GenerationWorkItem(payload, Guid.NewGuid(), DateTime.UtcNow, Priority: 5);
    }

    [Fact]
    public async Task Scenario1_HappyPath_CompletesSuccessfully()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CoreDbContext>().UseSqlite(connection).Options;
        using (var db = new CoreDbContext(options)) await db.Database.EnsureCreatedAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new CoreDbContext(options));
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton(GenerationRetryPolicy.Default);
        services.AddScoped<IImageGenerationOrchestrator>(_ => new ConfigurableOrchestrator(p => Task.FromResult(new JobExecutionResult(JobExecutionStatus.Completed))));

        var sp = services.BuildServiceProvider();
        using var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 10);
        var worker = new GenerationWorker(sp.GetRequiredService<IServiceScopeFactory>(), queue, NullLogger<GenerationWorker>.Instance, "worker-1");

        var item = CreateTestWorkItem();
        var result = await worker.ProcessWorkItemDirectAsync(item);
        Assert.Equal(JobExecutionStatus.Completed, result.Status);
    }

    [Fact]
    public async Task Scenario2_ProviderTimeout_ReturnsDeferred()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CoreDbContext>().UseSqlite(connection).Options;
        using (var db = new CoreDbContext(options)) await db.Database.EnsureCreatedAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new CoreDbContext(options));
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton(GenerationRetryPolicy.Deterministic(maxRetries: 3));
        services.AddScoped<IImageGenerationOrchestrator>(_ => new ConfigurableOrchestrator(p => throw new GpuTransientException("Timeout", 408)));

        var sp = services.BuildServiceProvider();
        using var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 10);
        var worker = new GenerationWorker(sp.GetRequiredService<IServiceScopeFactory>(), queue, NullLogger<GenerationWorker>.Instance, "worker-1");

        var item = CreateTestWorkItem();
        var result = await worker.ProcessWorkItemDirectAsync(item);
        Assert.Equal(JobExecutionStatus.Deferred, result.Status);
    }

    [Fact]
    public async Task Scenario5_InvalidWorkflow_FailsPermanentlyWithoutRetry()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CoreDbContext>().UseSqlite(connection).Options;
        using (var db = new CoreDbContext(options)) await db.Database.EnsureCreatedAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new CoreDbContext(options));
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton(GenerationRetryPolicy.Default);
        services.AddScoped<IImageGenerationOrchestrator>(_ => new ConfigurableOrchestrator(p => throw new GpuNonTransientException("Invalid syntax", 400)));

        var sp = services.BuildServiceProvider();
        using var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 10);
        var worker = new GenerationWorker(sp.GetRequiredService<IServiceScopeFactory>(), queue, NullLogger<GenerationWorker>.Instance, "worker-1");

        var item = CreateTestWorkItem();
        var result = await worker.ProcessWorkItemDirectAsync(item);
        Assert.Equal(JobExecutionStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Scenario8_DuplicateDelivery_IsSuppressedByQueue()
    {
        using var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 10);

        var reqId = Guid.NewGuid();
        var item1 = CreateTestWorkItem(requestId: reqId);
        var item2 = CreateTestWorkItem(requestId: reqId); // Duplicate

        await queue.EnqueueAsync(item1);
        await queue.EnqueueAsync(item2);

        Assert.Equal(1, queue.CurrentDepth); // Exactly 1 item in queue

        var deq = await queue.DequeueAsync();
        Assert.NotNull(deq);
        Assert.Equal(reqId, deq.Payload.GenerationRequestId);
        Assert.Equal(0, queue.CurrentDepth);
    }

    [Fact]
    public async Task Scenario9_Cancellation_HandlesCancellationGracefully()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CoreDbContext>().UseSqlite(connection).Options;
        using (var db = new CoreDbContext(options)) await db.Database.EnsureCreatedAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new CoreDbContext(options));
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton(GenerationRetryPolicy.Default);
        services.AddScoped<IImageGenerationOrchestrator>(_ => new ConfigurableOrchestrator(p => throw new OperationCanceledException()));

        var sp = services.BuildServiceProvider();
        using var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 10);
        var worker = new GenerationWorker(sp.GetRequiredService<IServiceScopeFactory>(), queue, NullLogger<GenerationWorker>.Instance, "worker-1");

        var item = CreateTestWorkItem();
        var result = await worker.ProcessWorkItemDirectAsync(item);
        Assert.Equal(JobExecutionStatus.Skipped, result.Status);
    }
}
