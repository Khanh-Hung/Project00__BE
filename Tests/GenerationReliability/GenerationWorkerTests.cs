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

public sealed class GenerationWorkerTests
{
    private sealed class FailingOrchestrator : IImageGenerationOrchestrator
    {
        private readonly Exception _exceptionToThrow;
        public FailingOrchestrator(Exception exceptionToThrow) => _exceptionToThrow = exceptionToThrow;

        public Task<JobExecutionResult> OrchestrateSceneImageGenerationAsync(
            SceneImageGenerationOutboxPayload payload,
            Guid outboxId,
            string workerId,
            DateTime now,
            CancellationToken ct = default)
        {
            throw _exceptionToThrow;
        }
    }

    [Fact]
    public async Task Worker_TransientError_SchedulesRetryWithBackoff()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var dbInit = new ProjectDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
        }

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
        services.AddSingleton(GenerationRetryPolicy.Deterministic(maxRetries: 3, baseDelay: TimeSpan.FromSeconds(2)));
        services.AddScoped<IImageGenerationOrchestrator>(_ => new FailingOrchestrator(new GpuTransientException("Timeout", 408)));

        var sp = services.BuildServiceProvider();
        var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 10);
        var worker = new GenerationWorker(
            scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
            jobQueue: queue,
            logger: NullLogger<GenerationWorker>.Instance,
            workerId: "worker-test"
        );

        var result = await worker.ProcessJobDirectAsync(job.Id);
        Assert.Equal(JobExecutionStatus.Deferred, result.Status);

        using (var dbVerify = new ProjectDbContext(options))
        {
            var verifiedJob = await dbVerify.ImageGenerationJobs.FirstAsync(j => j.Id == job.Id);
            Assert.Equal(ImageJobStatus.Queued, verifiedJob.Status);
            Assert.Equal(1, verifiedJob.RetryCount);
            Assert.True(verifiedJob.IsRetryable);
            Assert.NotNull(verifiedJob.NextAttemptAt);
        }
    }

    [Fact]
    public async Task Worker_PermanentError_FailsJobImmediately()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var dbInit = new ProjectDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
        }

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
        services.AddScoped<IImageGenerationOrchestrator>(_ => new FailingOrchestrator(new GpuNonTransientException("Invalid node syntax", 400)));

        var sp = services.BuildServiceProvider();
        var queue = new GenerationQueue(NullLogger<GenerationQueue>.Instance, 10);
        var worker = new GenerationWorker(
            scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
            jobQueue: queue,
            logger: NullLogger<GenerationWorker>.Instance,
            workerId: "worker-test"
        );

        var result = await worker.ProcessJobDirectAsync(job.Id);
        Assert.Equal(JobExecutionStatus.Failed, result.Status);

        using (var dbVerify = new ProjectDbContext(options))
        {
            var verifiedJob = await dbVerify.ImageGenerationJobs.FirstAsync(j => j.Id == job.Id);
            Assert.Equal(ImageJobStatus.Failed, verifiedJob.Status);
            Assert.Equal(0, verifiedJob.RetryCount);
            Assert.False(verifiedJob.IsRetryable);
        }
    }
}
