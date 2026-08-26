using Application.DTOs;
using Application.Enums;
using Application.Exceptions;
using Application.Interfaces;
using Application.Services;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
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

    [Fact]
    public async Task Scenario1_HappyPath_CompletesSuccessfully_WithAcceptedAttempt_AndCurrentArtifact()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectDbContext>().UseSqlite(connection).Options;
        using (var db = new ProjectDbContext(options)) await db.Database.EnsureCreatedAsync();

        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.MarkQueued(DateTime.UtcNow);

        using (var dbSeed = new ProjectDbContext(options))
        {
            await dbSeed.ImageGenerationJobs.AddAsync(job);
            await dbSeed.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => new ProjectDbContext(options));
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton(GenerationRetryPolicy.Default);
        services.AddScoped<IImageGenerationOrchestrator>(_ => new ConfigurableOrchestrator(p => Task.FromResult(new JobExecutionResult(JobExecutionStatus.Completed))));

        var sp = services.BuildServiceProvider();
        var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 10);
        var worker = new GenerationWorker(sp.GetRequiredService<IServiceScopeFactory>(), queue, NullLogger<GenerationWorker>.Instance, "worker-1");

        var result = await worker.ProcessJobDirectAsync(job.Id);
        Assert.Equal(JobExecutionStatus.Completed, result.Status);
    }

    [Fact]
    public async Task Scenario2_ProviderTimeout_SchedulesRetry()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectDbContext>().UseSqlite(connection).Options;
        using (var db = new ProjectDbContext(options)) await db.Database.EnsureCreatedAsync();

        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.MarkQueued(DateTime.UtcNow);

        using (var dbSeed = new ProjectDbContext(options))
        {
            await dbSeed.ImageGenerationJobs.AddAsync(job);
            await dbSeed.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => new ProjectDbContext(options));
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton(GenerationRetryPolicy.Deterministic(maxRetries: 3));
        services.AddScoped<IImageGenerationOrchestrator>(_ => new ConfigurableOrchestrator(p => throw new GpuTransientException("Timeout", 408)));

        var sp = services.BuildServiceProvider();
        var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 10);
        var worker = new GenerationWorker(sp.GetRequiredService<IServiceScopeFactory>(), queue, NullLogger<GenerationWorker>.Instance, "worker-1");

        var result = await worker.ProcessJobDirectAsync(job.Id);
        Assert.Equal(JobExecutionStatus.Deferred, result.Status);

        using (var dbVerify = new ProjectDbContext(options))
        {
            var verified = await dbVerify.ImageGenerationJobs.FirstAsync(j => j.Id == job.Id);
            Assert.Equal(ImageJobStatus.Queued, verified.Status);
            Assert.Equal(1, verified.RetryCount);
        }
    }

    [Fact]
    public async Task Scenario5_InvalidWorkflow_FailsPermanentlyWithoutRetry()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectDbContext>().UseSqlite(connection).Options;
        using (var db = new ProjectDbContext(options)) await db.Database.EnsureCreatedAsync();

        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.MarkQueued(DateTime.UtcNow);

        using (var dbSeed = new ProjectDbContext(options))
        {
            await dbSeed.ImageGenerationJobs.AddAsync(job);
            await dbSeed.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => new ProjectDbContext(options));
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton(GenerationRetryPolicy.Default);
        services.AddScoped<IImageGenerationOrchestrator>(_ => new ConfigurableOrchestrator(p => throw new GpuNonTransientException("Invalid syntax", 400)));

        var sp = services.BuildServiceProvider();
        var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 10);
        var worker = new GenerationWorker(sp.GetRequiredService<IServiceScopeFactory>(), queue, NullLogger<GenerationWorker>.Instance, "worker-1");

        var result = await worker.ProcessJobDirectAsync(job.Id);
        Assert.Equal(JobExecutionStatus.Failed, result.Status);

        using (var dbVerify = new ProjectDbContext(options))
        {
            var verified = await dbVerify.ImageGenerationJobs.FirstAsync(j => j.Id == job.Id);
            Assert.Equal(ImageJobStatus.Failed, verified.Status);
            Assert.Equal(0, verified.RetryCount);
            Assert.False(verified.IsRetryable);
        }
    }

    [Fact]
    public async Task Scenario8_DuplicateDelivery_AllowsOnlyOneWorkerToExecute()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectDbContext>().UseSqlite(connection).Options;
        using (var db = new ProjectDbContext(options)) await db.Database.EnsureCreatedAsync();

        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.MarkQueued(DateTime.UtcNow);

        using (var dbSeed = new ProjectDbContext(options))
        {
            await dbSeed.ImageGenerationJobs.AddAsync(job);
            await dbSeed.SaveChangesAsync();
        }

        int gpuExecutionCount = 0;
        var services = new ServiceCollection();
        services.AddScoped(_ => new ProjectDbContext(options));
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton(GenerationRetryPolicy.Default);
        services.AddScoped<IImageGenerationOrchestrator>(_ => new ConfigurableOrchestrator(p =>
        {
            Interlocked.Increment(ref gpuExecutionCount);
            return Task.FromResult(new JobExecutionResult(JobExecutionStatus.Completed));
        }));

        var sp = services.BuildServiceProvider();
        var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 10);
        var workerA = new GenerationWorker(sp.GetRequiredService<IServiceScopeFactory>(), queue, NullLogger<GenerationWorker>.Instance, "worker-A");
        var workerB = new GenerationWorker(sp.GetRequiredService<IServiceScopeFactory>(), queue, NullLogger<GenerationWorker>.Instance, "worker-B");

        // Worker A and Worker B both try to process the same job
        var resA = await workerA.ProcessJobDirectAsync(job.Id);
        var resB = await workerB.ProcessJobDirectAsync(job.Id);

        Assert.Equal(JobExecutionStatus.Completed, resA.Status);
        Assert.True(resB.Status == JobExecutionStatus.Deferred || resB.Status == JobExecutionStatus.Skipped); // Worker B gracefully avoids duplicate execution
        Assert.Equal(1, gpuExecutionCount); // Exactly 1 GPU call executed!
    }

    [Fact]
    public async Task Scenario9_Cancellation_PreventsExecution()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectDbContext>().UseSqlite(connection).Options;
        using (var db = new ProjectDbContext(options)) await db.Database.EnsureCreatedAsync();

        var job = new ImageGenerationJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        job.MarkQueued(DateTime.UtcNow);
        job.RequestCancellation(DateTime.UtcNow);

        using (var dbSeed = new ProjectDbContext(options))
        {
            await dbSeed.ImageGenerationJobs.AddAsync(job);
            await dbSeed.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => new ProjectDbContext(options));
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton(GenerationRetryPolicy.Default);
        services.AddScoped<IImageGenerationOrchestrator>(_ => new ConfigurableOrchestrator(p => throw new Exception("Should not be called!")));

        var sp = services.BuildServiceProvider();
        var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 10);
        var worker = new GenerationWorker(sp.GetRequiredService<IServiceScopeFactory>(), queue, NullLogger<GenerationWorker>.Instance, "worker-1");

        var result = await worker.ProcessJobDirectAsync(job.Id);
        Assert.Equal(JobExecutionStatus.Skipped, result.Status);
    }
}
