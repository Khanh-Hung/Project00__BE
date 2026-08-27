using System.Text.Json;
using Application.DTOs;
using Application.Exceptions;
using Application.Interfaces;
using Application.Services;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.GenerationProduction;

[Collection("NonParallelMetricsCollection")]
public sealed class GenerationTerminalFailureIntegrationTests
{
    private sealed class MockImageService : IImageGenerationService
    {
        public Func<ImageGenerationRequest, CancellationToken, Task<string>> Handler { get; set; }

        public MockImageService(Func<ImageGenerationRequest, CancellationToken, Task<string>>? handler = null)
        {
            Handler = handler ?? ((req, ct) => Task.FromResult($"https://cdn.project00.ai/rendered_{Guid.NewGuid():N}.png"));
        }

        public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default)
            => Handler(new ImageGenerationRequest(prompt, width, height), ct);

        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default)
            => Handler(request, ct);
    }

    private sealed class DummyVoiceCompiler : IVoicePromptCompiler
    {
        public VoiceGenerationRequest CompileVoiceRequest(VoiceContext context)
            => new(CleanedText: context.RawText, VoiceId: context.Voice.VoiceId);

        public string ExtractCleanDialogueText(string rawReply) => rawReply;
    }

    private sealed class DummyVoiceService : IVoiceGenerationService
    {
        public Task<VoiceGenerationResult> GenerateVoiceAsync(VoiceGenerationRequest request, CancellationToken ct = default)
            => Task.FromResult(new VoiceGenerationResult(AudioUrl: "https://cdn.project00.ai/dummy.mp3"));
    }

    private sealed class DummyVisualCompiler : IVisualPromptCompiler
    {
        public string CompileScenePrompt(VisualSnapshot snapshot)
            => $"1girl, Elysia, outfit: {snapshot.SceneState.CurrentOutfit}, location: {snapshot.SceneState.CurrentLocation}";

        public string CompileScenePrompt(Character character, SceneContext context, CharacterRelationship? relationship, Domain.Enums.Slot2Context slot2Context = Domain.Enums.Slot2Context.SameScene)
            => $"1girl, {character.Name}";

        public string CompileAvatarPrompt(Character character)
            => $"1girl, avatar {character.Name}";

        public string CompileNegativePrompt(VisualSnapshot snapshot, string? customNegative = null)
            => "low quality, blurry";

        public string CompileNegativePrompt(CharacterVisualIdentity? identity, string? customNegative = null)
            => "low quality, blurry";
    }

    private sealed class DummyMemoryTrigger : IMemoryExtractionTrigger
    {
        public bool NotifyMessageSent(MemoryExtractionJob job) => true;
    }

    private (IServiceScopeFactory ScopeFactory, ProjectDbContext DbContext, MockImageService ImageService) CreateTestContext(string dbName)
    {
        var services = new ServiceCollection();
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        services.AddScoped(_ => new ProjectDbContext(options));
        services.AddScoped<IVoicePromptCompiler, DummyVoiceCompiler>();
        services.AddScoped<IVisualPromptCompiler, DummyVisualCompiler>();
        services.AddScoped<IVoiceGenerationService, DummyVoiceService>();
        services.AddScoped<IMemoryExtractionTrigger, DummyMemoryTrigger>();

        var imageService = new MockImageService();
        services.AddScoped<IImageGenerationService>(_ => imageService);

        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var dbContext = serviceProvider.GetRequiredService<ProjectDbContext>();

        return (scopeFactory, dbContext, imageService);
    }

    private static VisualSnapshot CreateSnapshot(Guid sessionId, Guid turnId, int revision)
    {
        var visualIdentity = new CharacterVisualIdentity(
            Hair: "platinum blonde",
            Eyes: "green",
            Body: "slender",
            CanonicalReferenceUrl: "https://files.catbox.moe/g2343q.png"
        );
        var sceneState = new SessionSceneState(
            CurrentLocation: "Living Room",
            CurrentOutfit: "White Dress",
            Atmosphere: "Warm indoor daylight",
            SceneRevision: revision
        );
        return new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: Guid.NewGuid(),
            SceneRevision: revision,
            VisualIdentity: visualIdentity,
            SceneState: sceneState,
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: 12345L)
        );
    }

    [Fact]
    public async Task GpuNonTransientException_RoutesThroughWorker_PersistsJobFailed_WithIsRetryableFalse_AndNeverReDispatched()
    {
        var dbName = Guid.NewGuid().ToString();
        var (scopeFactory, db, imageService) = CreateTestContext(dbName);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var snapshot = CreateSnapshot(sessionId, turnId, revision: 1);
        var payload = JsonSerializer.Serialize(new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, Guid.NewGuid()));
        var msg = new OutboxMessage(OutboxEventTypes.SceneImageGeneration, payload);
        await db.OutboxMessages.AddAsync(msg);
        await db.SaveChangesAsync();

        // 1. Configure image service to throw a non-transient error (e.g. 400 Bad Request / InvalidWorkflow)
        imageService.Handler = (req, ct) => throw new GpuNonTransientException("Invalid workflow schema.", 400);

        var processor = new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance);
        var processedFirstPass = await processor.ProcessPendingOutboxMessagesAsync();

        Assert.True(processedFirstPass >= 1);

        // 2. Assert OutboxMessage state: fast-failed immediately without retries
        var updatedMsg = await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == msg.Id);
        Assert.Equal(OutboxStatus.Failed, updatedMsg.Status);
        Assert.Equal(3, updatedMsg.RetryCount); // MaxRetries
        Assert.Null(updatedMsg.NextRetryAt);

        // 3. Assert ImageGenerationJob state: Failed and IsRetryable = false
        var job = await db.ImageGenerationJobs.AsNoTracking().FirstOrDefaultAsync(j => j.TurnId == turnId);
        Assert.NotNull(job);
        Assert.Equal(ImageJobStatus.Failed, job.Status);
        Assert.False(job.IsRetryable);
        Assert.NotNull(job.CompletedAt);
        Assert.Null(job.LeaseUntil);

        // 4. Run second pass of Outbox processor: proves NO SceneImageGeneration re-dispatch occurs
        await processor.ProcessPendingOutboxMessagesAsync();
        var pendingSceneMessages = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.EventType == OutboxEventTypes.SceneImageGeneration && m.Status == OutboxStatus.Pending)
            .ToListAsync();
        Assert.Empty(pendingSceneMessages);

        // 5. Verify that another worker cannot claim the terminal job
        var secondClaim = job.TryClaim("worker-2", TimeSpan.FromMinutes(5), DateTime.UtcNow);
        Assert.False(secondClaim);
    }

    [Fact]
    public async Task CanRetryFailure_False_WhenMaxAttemptsExhausted_PersistsJobFailed_WithIsRetryableFalse_AndWorkerFastFailsOutbox()
    {
        var dbName = Guid.NewGuid().ToString();
        var (scopeFactory, db, imageService) = CreateTestContext(dbName);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var genRequestId = Guid.NewGuid();
        var snapshot = CreateSnapshot(sessionId, turnId, revision: 1);
        var payload = JsonSerializer.Serialize(new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, genRequestId));
        var msg = new OutboxMessage(OutboxEventTypes.SceneImageGeneration, payload);
        await db.OutboxMessages.AddAsync(msg);

        // Pre-seed job at attempt 3 with expired lease so current worker claims it cleanly
        var past = DateTime.UtcNow.AddMinutes(-30);
        var existingJob = new ImageGenerationJob(sessionId, turnId, snapshot.CharacterId, sceneRevision: 1, generationRequestId: genRequestId, outboxMessageId: msg.Id);
        existingJob.TryClaim("old-worker", TimeSpan.FromMinutes(5), past);
        existingJob.TryClaim("old-worker", TimeSpan.FromMinutes(5), past.AddMinutes(6));
        existingJob.TryClaim("old-worker", TimeSpan.FromMinutes(5), past.AddMinutes(12));
        await db.ImageGenerationJobs.AddAsync(existingJob);
        await db.SaveChangesAsync();

        // 1. Configure image service to throw a transient error (e.g. 500), but budget is exhausted (attempt = 3)
        imageService.Handler = (req, ct) => throw new GpuTransientException("GPU 500 error", 500);

        var processor = new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance);
        var processedFirstPass = await processor.ProcessPendingOutboxMessagesAsync();

        Assert.True(processedFirstPass >= 1);

        // 2. Assert OutboxMessage state: fast-failed because CanRetryFailure was false
        var updatedMsg = await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == msg.Id);
        Assert.Equal(OutboxStatus.Failed, updatedMsg.Status);
        Assert.Null(updatedMsg.NextRetryAt);

        // 3. Assert ImageGenerationJob state: Failed and IsRetryable = false
        var reloadedJob = await db.ImageGenerationJobs.AsNoTracking().FirstAsync(j => j.Id == existingJob.Id);
        Assert.Equal(ImageJobStatus.Failed, reloadedJob.Status);
        Assert.False(reloadedJob.IsRetryable);
        Assert.NotNull(reloadedJob.CompletedAt);

        // 4. Second pass verifies no re-dispatch
        await processor.ProcessPendingOutboxMessagesAsync();
        var pendingSceneMessages = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.EventType == OutboxEventTypes.SceneImageGeneration && m.Status == OutboxStatus.Pending)
            .ToListAsync();
        Assert.Empty(pendingSceneMessages);
    }

    [Fact]
    public async Task CanRetryFailure_True_WhenTransientFailureWithinBudget_PersistsJobQueued_WithAuthoritativeNextAttemptAt_AndWorkerSchedulesOutboxRetry()
    {
        var dbName = Guid.NewGuid().ToString();
        var (scopeFactory, db, imageService) = CreateTestContext(dbName);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var genRequestId = Guid.NewGuid();
        var snapshot = CreateSnapshot(sessionId, turnId, revision: 1);
        var payload = JsonSerializer.Serialize(new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, genRequestId));
        var msg = new OutboxMessage(OutboxEventTypes.SceneImageGeneration, payload);
        await db.OutboxMessages.AddAsync(msg);
        await db.SaveChangesAsync();

        // 1. Configure image service to throw a transient error (e.g. 500), within budget (attempt = 1)
        imageService.Handler = (req, ct) => throw new GpuTransientException("Transient 500 error", 500);

        var processor = new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance);
        var processed = await processor.ProcessPendingOutboxMessagesAsync();

        Assert.True(processed >= 1);

        // 2. Assert OutboxMessage state: Scheduled for retry (Pending with NextRetryAt in future)
        var updatedMsg = await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == msg.Id);
        Assert.Equal(OutboxStatus.Pending, updatedMsg.Status);
        Assert.Equal(1, updatedMsg.RetryCount);
        Assert.NotNull(updatedMsg.NextRetryAt);
        Assert.True(updatedMsg.NextRetryAt > DateTime.UtcNow.AddMilliseconds(-100));

        // 3. Assert ImageGenerationJob state: Queued with authoritative NextAttemptAt and IsRetryable = true
        var job = await db.ImageGenerationJobs.AsNoTracking().FirstOrDefaultAsync(j => j.TurnId == turnId);
        Assert.NotNull(job);
        Assert.Equal(ImageJobStatus.Queued, job.Status);
        Assert.True(job.IsRetryable);
        Assert.Equal(1, job.RetryCount);
        Assert.NotNull(job.NextAttemptAt);
        Assert.Null(job.ClaimedBy); // Released for next worker pickup
        Assert.Null(job.LeaseUntil);
    }

    [Fact]
    public async Task RetryBudget_MultiDispatch_EnforcesGlobal90sDeadline_AndTerminatesWhenPersistedStartedAtExceeds90s()
    {
        var dbName = Guid.NewGuid().ToString();
        var (scopeFactory, db, imageService) = CreateTestContext(dbName);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var genRequestId = Guid.NewGuid();
        var snapshot = CreateSnapshot(sessionId, turnId, revision: 1);
        var payload = JsonSerializer.Serialize(new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, genRequestId));
        var msg = new OutboxMessage(OutboxEventTypes.SceneImageGeneration, payload);
        await db.OutboxMessages.AddAsync(msg);

        // Pre-seed job with StartedAt set to 95 seconds ago (exceeding the 90s global budget across previous dispatches)
        var past95s = DateTime.UtcNow.AddSeconds(-95);
        var existingJob = new ImageGenerationJob(sessionId, turnId, snapshot.CharacterId, sceneRevision: 1, generationRequestId: genRequestId, outboxMessageId: msg.Id);
        existingJob.TryClaim("old-worker", TimeSpan.FromMinutes(1), past95s);
        // Expire lease
        await db.ImageGenerationJobs.AddAsync(existingJob);
        await db.SaveChangesAsync();

        // Configure image service to throw transient error
        imageService.Handler = (req, ct) => throw new GpuTransientException("Transient 500 error", 500);

        var processor = new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance);
        var processed = await processor.ProcessPendingOutboxMessagesAsync();

        Assert.True(processed >= 1);

        // Assert job terminated because global wall-clock exceeded 90s
        var reloadedJob = await db.ImageGenerationJobs.AsNoTracking().FirstAsync(j => j.Id == existingJob.Id);
        Assert.Equal(ImageJobStatus.Failed, reloadedJob.Status);
        Assert.False(reloadedJob.IsRetryable);
        Assert.NotNull(reloadedJob.CompletedAt);

        // Outbox fast-failed
        var updatedMsg = await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == msg.Id);
        Assert.Equal(OutboxStatus.Failed, updatedMsg.Status);
    }
}
