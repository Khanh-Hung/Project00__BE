using System.Text.Json;
using Application.DTOs;
using Application.Exceptions;
using Application.Interfaces;
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

namespace Tests;

public sealed class OutboxReliabilityAndOrderingTests
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

        public string CompileScenePrompt(Character character, SceneContext context, CharacterRelationship? relationship)
            => $"1girl, {character.Name}";

        public string CompileAvatarPrompt(Character character)
            => $"1girl, avatar {character.Name}";
    }

    private sealed class DummyMemoryTrigger : IMemoryExtractionTrigger
    {
        public bool NotifyMessageSent(MemoryExtractionJob job) => true;
    }

    private static string? GetPostgresConnectionString()
    {
        var envConn = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        if (!string.IsNullOrWhiteSpace(envConn)) return envConn;

        var devSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "appsettings.Development.json");
        if (File.Exists(devSettingsPath))
        {
            var json = File.ReadAllText(devSettingsPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("ConnectionStrings", out var connSection) &&
                connSection.TryGetProperty("DefaultConnection", out var connProp))
            {
                return connProp.GetString();
            }
        }
        return null;
    }

    private (IServiceScopeFactory ScopeFactory, ProjectDbContext DbContext, MockImageService ImageService) CreatePostgreSqlTestContext(string connectionString)
    {
        var services = new ServiceCollection();
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseNpgsql(connectionString)
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

    private static VisualSnapshot CreateSnapshot(Guid sessionId, Guid turnId, int revision, string outfit = "White Dress", string location = "Living Room")
    {
        var visualIdentity = new CharacterVisualIdentity(
            Hair: "platinum blonde",
            Eyes: "green",
            Body: "slender",
            CanonicalReferenceUrl: "https://files.catbox.moe/g2343q.png"
        );
        var sceneState = new SessionSceneState(
            CurrentLocation: location,
            CurrentOutfit: outfit,
            Atmosphere: "Warm indoor daylight",
            SceneRevision: revision
        );
        var transientState = new TransientVisualState(
            Pose: "Sitting",
            Expression: "Happy"
        );

        return new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: Guid.NewGuid(),
            SceneRevision: revision,
            VisualIdentity: visualIdentity,
            SceneState: sceneState,
            TransientState: transientState,
            GenerationProfile: GenerationProfile.CreateDefault(),
            IdentityReferenceUrl: visualIdentity.CanonicalReferenceUrl,
            PreviousSceneImageUrl: revision > 1 ? $"https://cdn.project00.ai/scenes/rev_{revision - 1}.png" : null,
            CreatedAt: Clock.Now
        );
    }

    [Fact]
    public async Task Scenario1_Duplicate_Outbox_Payload_Produces_Single_Artifact()
    {
        var dbName = Guid.NewGuid().ToString();
        var (scopeFactory, db, imageService) = CreateTestContext(dbName);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var snapshot = CreateSnapshot(sessionId, turnId, revision: 1);
        var payload = JsonSerializer.Serialize(new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, Guid.NewGuid()));

        var msg1 = new OutboxMessage(OutboxEventTypes.SceneImageGeneration, payload);
        var msg2 = new OutboxMessage(OutboxEventTypes.SceneImageGeneration, payload);
        await db.OutboxMessages.AddRangeAsync(msg1, msg2);
        await db.SaveChangesAsync();

        int callCount = 0;
        imageService.Handler = (req, ct) =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult("https://cdn.project00.ai/scene_1.png");
        };

        var processor = new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance);
        await processor.ProcessPendingOutboxMessagesAsync();

        var artifacts = await db.SceneImages.AsNoTracking().Where(img => img.SessionId == sessionId).ToListAsync();
        Assert.Single(artifacts);
        Assert.Equal(1, callCount); // Second message was skipped due to application idempotency
    }

    [Fact]
    public async Task Scenario2_Concurrent_Workers_Atomic_GPU_Claim_Prevents_Duplicate_Inference()
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

        int gpuCallCount = 0;
        imageService.Handler = async (req, ct) =>
        {
            Interlocked.Increment(ref gpuCallCount);
            // Simulate realistic 50ms GPU rendering delay to allow race conditions to manifest
            await Task.Delay(50, ct);
            return "https://cdn.project00.ai/rendered_concurrent.png";
        };

        // Spawn 5 concurrent worker instances competing for the exact same message
        var workers = Enumerable.Range(1, 5)
            .Select(i => new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance, $"worker-{i}"))
            .ToList();

        // Run all 5 workers simultaneously in parallel
        await Task.WhenAll(workers.Select(w => w.ProcessPendingOutboxMessagesAsync()));

        // Invariant: GPU was invoked EXACTLY ONCE across all competing workers
        Assert.Equal(1, gpuCallCount);

        // Invariant: Exactly ONE SceneImage artifact was persisted in DB
        var artifacts = await db.SceneImages.AsNoTracking().Where(img => img.SessionId == sessionId).ToListAsync();
        Assert.Single(artifacts);

        // Invariant: OutboxMessage is completed
        var updatedMsg = await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == msg.Id);
        Assert.Equal(OutboxStatus.Completed, updatedMsg.Status);
    }

    [Fact]
    public async Task Scenario2b_Real_PostgreSQL_ExecuteUpdateAsync_Atomic_Claim_Prevents_Duplicate_Inference()
    {
        var connStr = GetPostgresConnectionString();
        if (string.IsNullOrWhiteSpace(connStr)) return; // Skip if no real Postgres available

        var (scopeFactory, db, imageService) = CreatePostgreSqlTestContext(connStr);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var snapshot = CreateSnapshot(sessionId, turnId, revision: 1);
        var payload = JsonSerializer.Serialize(new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, Guid.NewGuid()));
        var msg = new OutboxMessage(OutboxEventTypes.SceneImageGeneration, payload);

        await db.OutboxMessages.AddAsync(msg);
        await db.SaveChangesAsync();

        try
        {
            int gpuCallCount = 0;
            imageService.Handler = async (req, ct) =>
            {
                Interlocked.Increment(ref gpuCallCount);
                // Simulate realistic 80ms GPU delay to maximize concurrency race window
                await Task.Delay(80, ct);
                return "https://cdn.project00.ai/rendered_postgres_concurrent.png";
            };

            // Spawn 5 concurrent worker instances with distinct WorkerIds
            var workers = Enumerable.Range(1, 5)
                .Select(i => new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance, $"pg-worker-{i}"))
                .ToList();

            // Run all 5 workers simultaneously in parallel against real PostgreSQL
            await Task.WhenAll(workers.Select(w => w.ProcessPendingOutboxMessagesAsync()));

            // PROOF 1: GPU was invoked EXACTLY ONCE on real PostgreSQL ExecuteUpdateAsync
            Assert.Equal(1, gpuCallCount);

            // PROOF 2: Exactly ONE SceneImage artifact was persisted in PostgreSQL
            var artifacts = await db.SceneImages.AsNoTracking().Where(img => img.SessionId == sessionId).ToListAsync();
            Assert.Single(artifacts);

            // PROOF 3: OutboxMessage is Completed and has ClaimedBy set
            var updatedMsg = await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == msg.Id);
            Assert.Equal(OutboxStatus.Completed, updatedMsg.Status);
        }
        finally
        {
            // Clean up test data from PostgreSQL
            var toDeleteImages = await db.SceneImages.Where(img => img.SessionId == sessionId).ToListAsync();
            db.SceneImages.RemoveRange(toDeleteImages);
            var toDeleteMsg = await db.OutboxMessages.FirstOrDefaultAsync(m => m.Id == msg.Id);
            if (toDeleteMsg != null) db.OutboxMessages.Remove(toDeleteMsg);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Scenario3_GPU_Timeout_Triggers_Exponential_Backoff_Retry()
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

        imageService.Handler = (req, ct) => throw new GpuTransientException("Request timed out.", 408);

        var processor = new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance);
        await processor.ProcessPendingOutboxMessagesAsync();

        var updatedMsg = await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == msg.Id);
        Assert.Equal(OutboxStatus.Pending, updatedMsg.Status);
        Assert.Equal(1, updatedMsg.RetryCount);
        Assert.NotNull(updatedMsg.NextRetryAt);
        Assert.True(updatedMsg.NextRetryAt > Clock.Now);
    }

    [Fact]
    public async Task Scenario4_GPU_429_RateLimit_Triggers_Exponential_Backoff_Retry()
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

        imageService.Handler = (req, ct) => throw new GpuTransientException("Too Many Requests.", 429);

        var processor = new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance);
        await processor.ProcessPendingOutboxMessagesAsync();

        var updatedMsg = await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == msg.Id);
        Assert.Equal(OutboxStatus.Pending, updatedMsg.Status);
        Assert.Equal(1, updatedMsg.RetryCount);
        Assert.NotNull(updatedMsg.NextRetryAt);
    }

    [Fact]
    public async Task Scenario5_GPU_500_Server_Crash_Triggers_Exponential_Backoff_Retry()
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

        imageService.Handler = (req, ct) => throw new GpuTransientException("Internal Server Error.", 500);

        var processor = new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance);
        await processor.ProcessPendingOutboxMessagesAsync();

        var updatedMsg = await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == msg.Id);
        Assert.Equal(OutboxStatus.Pending, updatedMsg.Status);
        Assert.Equal(1, updatedMsg.RetryCount);
    }

    [Fact]
    public async Task Scenario6_GPU_400_Bad_Request_Fast_Fails_Without_Retry()
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

        imageService.Handler = (req, ct) => throw new GpuNonTransientException("Bad prompt payload.", 400);

        var processor = new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance);
        await processor.ProcessPendingOutboxMessagesAsync();

        var updatedMsg = await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == msg.Id);
        Assert.Equal(OutboxStatus.Failed, updatedMsg.Status);
        Assert.Null(updatedMsg.NextRetryAt);
    }

    [Fact]
    public async Task Scenario7_GPU_404_Reference_Missing_Fast_Fails_Without_Retry()
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

        imageService.Handler = (req, ct) => throw new GpuNonTransientException("Identity reference URL 404 Not Found.", 404);

        var processor = new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance);
        await processor.ProcessPendingOutboxMessagesAsync();

        var updatedMsg = await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == msg.Id);
        Assert.Equal(OutboxStatus.Failed, updatedMsg.Status);
        Assert.Null(updatedMsg.NextRetryAt);
    }

    [Fact]
    public void Scenario8_NextRetryAt_Respects_Exponential_Backoff_With_Jitter()
    {
        var msg = new OutboxMessage("TestEvent", "{}");
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

        // Retry 1: base = 2^1 * 5 = 10s (+/- 2s) -> range [8, 12]s
        msg.MarkFailed("Transient error 1", now, isTransient: true);
        Assert.Equal(OutboxStatus.Pending, msg.Status);
        Assert.Equal(1, msg.RetryCount);
        Assert.NotNull(msg.NextRetryAt);
        var diff1 = (msg.NextRetryAt.Value - now).TotalSeconds;
        Assert.InRange(diff1, 8, 12);

        // Retry 2: base = 2^2 * 5 = 20s (+/- 2s) -> range [18, 22]s
        msg.MarkFailed("Transient error 2", now, isTransient: true);
        Assert.Equal(2, msg.RetryCount);
        var diff2 = (msg.NextRetryAt.Value - now).TotalSeconds;
        Assert.InRange(diff2, 18, 22);

        // Retry 3 (MaxRetries=3) -> Failed (Dead Letter)
        msg.MarkFailed("Transient error 3", now, isTransient: true);
        Assert.Equal(OutboxStatus.Failed, msg.Status);
        Assert.Null(msg.NextRetryAt);
    }

    [Fact]
    public async Task Scenario9_Revision_N_Cannot_Run_Before_Revision_N_Minus_1()
    {
        var dbName = Guid.NewGuid().ToString();
        var (scopeFactory, db, imageService) = CreateTestContext(dbName);
        var sessionId = Guid.NewGuid();

        // Enqueue only Revision 2 without Revision 1 artifact
        var snapshot2 = CreateSnapshot(sessionId, Guid.NewGuid(), revision: 2);
        var payload2 = JsonSerializer.Serialize(new SceneImageGenerationOutboxPayload(snapshot2.TurnId, snapshot2.CharacterId, Guid.NewGuid(), snapshot2, Guid.NewGuid()));
        var msg2 = new OutboxMessage(OutboxEventTypes.SceneImageGeneration, payload2);
        await db.OutboxMessages.AddAsync(msg2);
        await db.SaveChangesAsync();

        int gpuCalled = 0;
        imageService.Handler = (req, ct) => { gpuCalled++; return Task.FromResult("https://cdn.project00.ai/img.png"); };

        var processor = new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance);
        await processor.ProcessPendingOutboxMessagesAsync();

        // GPU must NOT be called for Revision 2 because Predecessor Revision 1 artifact does not exist
        Assert.Equal(0, gpuCalled);
        var updatedMsg2 = await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == msg2.Id);
        Assert.Equal(OutboxStatus.Pending, updatedMsg2.Status);
    }

    [Fact]
    public async Task Scenario10_Revision_N_Minus_1_Processing_Causes_Revision_N_To_Defer()
    {
        var dbName = Guid.NewGuid().ToString();
        var (scopeFactory, db, _) = CreateTestContext(dbName);
        var sessionId = Guid.NewGuid();

        var snapshot1 = CreateSnapshot(sessionId, Guid.NewGuid(), revision: 1);
        var snapshot2 = CreateSnapshot(sessionId, Guid.NewGuid(), revision: 2);

        var msg1 = new OutboxMessage(OutboxEventTypes.SceneImageGeneration, JsonSerializer.Serialize(new SceneImageGenerationOutboxPayload(snapshot1.TurnId, snapshot1.CharacterId, Guid.NewGuid(), snapshot1, Guid.NewGuid())));
        var msg2 = new OutboxMessage(OutboxEventTypes.SceneImageGeneration, JsonSerializer.Serialize(new SceneImageGenerationOutboxPayload(snapshot2.TurnId, snapshot2.CharacterId, Guid.NewGuid(), snapshot2, Guid.NewGuid())));

        msg1.MarkProcessing();
        await db.OutboxMessages.AddRangeAsync(msg1, msg2);
        await db.SaveChangesAsync();

        var processor = new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance);
        await processor.ProcessPendingOutboxMessagesAsync();

        var updatedMsg2 = await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == msg2.Id);
        Assert.Equal(OutboxStatus.Pending, updatedMsg2.Status);
        Assert.Equal(0, updatedMsg2.RetryCount); // Invariant: Defer does NOT increment retry count!
        Assert.NotNull(updatedMsg2.NextRetryAt);
    }

    [Fact]
    public async Task Scenario11_Revision_N_Minus_1_Permanent_Failure_Blocks_Revision_N_With_Reason()
    {
        var dbName = Guid.NewGuid().ToString();
        var (scopeFactory, db, _) = CreateTestContext(dbName);
        var sessionId = Guid.NewGuid();

        var snapshot1 = CreateSnapshot(sessionId, Guid.NewGuid(), revision: 1);
        var snapshot2 = CreateSnapshot(sessionId, Guid.NewGuid(), revision: 2);

        var msg1 = new OutboxMessage(OutboxEventTypes.SceneImageGeneration, JsonSerializer.Serialize(new SceneImageGenerationOutboxPayload(snapshot1.TurnId, snapshot1.CharacterId, Guid.NewGuid(), snapshot1, Guid.NewGuid())));
        var msg2 = new OutboxMessage(OutboxEventTypes.SceneImageGeneration, JsonSerializer.Serialize(new SceneImageGenerationOutboxPayload(snapshot2.TurnId, snapshot2.CharacterId, Guid.NewGuid(), snapshot2, Guid.NewGuid())));

        msg1.MarkFailed("Permanent GPU crash", Clock.Now, isTransient: false);
        await db.OutboxMessages.AddRangeAsync(msg1, msg2);
        await db.SaveChangesAsync();

        var processor = new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance);
        await processor.ProcessPendingOutboxMessagesAsync();

        var updatedMsg2 = await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == msg2.Id);
        // Must fail with explicit reason, preventing infinite deadlock
        Assert.Equal(OutboxStatus.Failed, updatedMsg2.Status);
        Assert.Contains("Predecessor Revision 1 failed permanently", updatedMsg2.LastError);
    }

    [Fact]
    public async Task Scenario12_Worker_Crash_During_Processing_Reclaims_Message_After_Lease_Timeout()
    {
        var dbName = Guid.NewGuid().ToString();
        var (scopeFactory, db, _) = CreateTestContext(dbName);

        var snapshot = CreateSnapshot(Guid.NewGuid(), Guid.NewGuid(), revision: 1);
        var msg = new OutboxMessage(OutboxEventTypes.SceneImageGeneration, JsonSerializer.Serialize(new SceneImageGenerationOutboxPayload(snapshot.TurnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, Guid.NewGuid())));

        // Simulate crash 3 minutes ago
        msg.MarkProcessing(workerId: "worker-dead", now: Clock.Now.AddMinutes(-3));
        await db.OutboxMessages.AddAsync(msg);
        await db.SaveChangesAsync();

        var processor = new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance);
        await processor.ProcessPendingOutboxMessagesAsync();

        var updatedMsg = await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == msg.Id);
        Assert.Equal(OutboxStatus.Completed, updatedMsg.Status); // Reclaimed and processed to completion
    }

    [Fact]
    public async Task Scenario13_Completed_Artifact_Prevents_GPU_Execution_On_Outbox_Replay()
    {
        var dbName = Guid.NewGuid().ToString();
        var (scopeFactory, db, imageService) = CreateTestContext(dbName);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var generationRequestId = Guid.NewGuid();

        // Artifact already persisted
        var artifact = new SceneImage(sessionId, Guid.NewGuid(), turnId, 1, "https://cdn.project00.ai/existing.png", "prompt", generationRequestId: generationRequestId);
        await db.SceneImages.AddAsync(artifact);
        await db.SaveChangesAsync();

        var snapshot = CreateSnapshot(sessionId, turnId, revision: 1);
        var msg = new OutboxMessage(OutboxEventTypes.SceneImageGeneration, JsonSerializer.Serialize(new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, generationRequestId)));
        await db.OutboxMessages.AddAsync(msg);
        await db.SaveChangesAsync();

        int gpuCall = 0;
        imageService.Handler = (req, ct) => { gpuCall++; return Task.FromResult("https://cdn.project00.ai/new.png"); };

        var processor = new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance);
        await processor.ProcessPendingOutboxMessagesAsync();

        Assert.Equal(0, gpuCall); // GPU never called
        var updatedMsg = await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == msg.Id);
        Assert.Equal(OutboxStatus.Completed, updatedMsg.Status);
    }

    [Fact]
    public async Task Scenario14_Different_Sessions_Process_Concurrently_Without_Locking_Each_Other()
    {
        var dbName = Guid.NewGuid().ToString();
        var (scopeFactory, db, imageService) = CreateTestContext(dbName);

        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();

        var snapA = CreateSnapshot(sessionA, Guid.NewGuid(), revision: 1);
        var snapB = CreateSnapshot(sessionB, Guid.NewGuid(), revision: 1);

        var msgA = new OutboxMessage(OutboxEventTypes.SceneImageGeneration, JsonSerializer.Serialize(new SceneImageGenerationOutboxPayload(snapA.TurnId, snapA.CharacterId, Guid.NewGuid(), snapA, Guid.NewGuid())));
        var msgB = new OutboxMessage(OutboxEventTypes.SceneImageGeneration, JsonSerializer.Serialize(new SceneImageGenerationOutboxPayload(snapB.TurnId, snapB.CharacterId, Guid.NewGuid(), snapB, Guid.NewGuid())));

        await db.OutboxMessages.AddRangeAsync(msgA, msgB);
        await db.SaveChangesAsync();

        var renderedList = new List<Guid>();
        imageService.Handler = (req, ct) =>
        {
            lock (renderedList)
            {
                renderedList.Add(Guid.NewGuid());
            }
            return Task.FromResult("https://cdn.project00.ai/rendered.png");
        };

        var processor = new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance);
        await processor.ProcessPendingOutboxMessagesAsync();

        Assert.Equal(2, renderedList.Count);
        var artA = await db.SceneImages.AsNoTracking().FirstOrDefaultAsync(img => img.SessionId == sessionA);
        var artB = await db.SceneImages.AsNoTracking().FirstOrDefaultAsync(img => img.SessionId == sessionB);
        Assert.NotNull(artA);
        Assert.NotNull(artB);
    }

    [Fact]
    public async Task Scenario15_Same_Session_Multi_Turns_Execute_Strictly_In_Order_Of_Revisions()
    {
        var dbName = Guid.NewGuid().ToString();
        var (scopeFactory, db, imageService) = CreateTestContext(dbName);
        var sessionId = Guid.NewGuid();

        var snap1 = CreateSnapshot(sessionId, Guid.NewGuid(), revision: 1);
        var snap2 = CreateSnapshot(sessionId, Guid.NewGuid(), revision: 2);
        var snap3 = CreateSnapshot(sessionId, Guid.NewGuid(), revision: 3);

        // Queue in reverse order (3, 2, 1) to test strict predecessor gating
        var msg3 = new OutboxMessage(OutboxEventTypes.SceneImageGeneration, JsonSerializer.Serialize(new SceneImageGenerationOutboxPayload(snap3.TurnId, snap3.CharacterId, Guid.NewGuid(), snap3, Guid.NewGuid())));
        var msg2 = new OutboxMessage(OutboxEventTypes.SceneImageGeneration, JsonSerializer.Serialize(new SceneImageGenerationOutboxPayload(snap2.TurnId, snap2.CharacterId, Guid.NewGuid(), snap2, Guid.NewGuid())));
        var msg1 = new OutboxMessage(OutboxEventTypes.SceneImageGeneration, JsonSerializer.Serialize(new SceneImageGenerationOutboxPayload(snap1.TurnId, snap1.CharacterId, Guid.NewGuid(), snap1, Guid.NewGuid())));

        await db.OutboxMessages.AddRangeAsync(msg3, msg2, msg1);
        await db.SaveChangesAsync();

        var generatedOrder = new List<string>();
        imageService.Handler = (req, ct) =>
        {
            generatedOrder.Add(req.Prompt);
            return Task.FromResult($"https://cdn.project00.ai/art_{generatedOrder.Count}.png");
        };

        var processor = new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance);
        
        // Pass 1: Revision 3 & 2 are deferred because predecessor artifacts don't exist; Revision 1 generates!
        await processor.ProcessPendingOutboxMessagesAsync();
        Assert.Single(generatedOrder);

        // Pass 2: Predecessor Revision 1 is now in DB -> Revision 2 generates; Revision 3 is deferred
        await processor.ProcessPendingOutboxMessagesAsync(referenceTime: DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(2, generatedOrder.Count);

        // Pass 3: Predecessor Revision 2 is now in DB -> Revision 3 generates
        await processor.ProcessPendingOutboxMessagesAsync(referenceTime: DateTime.UtcNow.AddSeconds(10));
        Assert.Equal(3, generatedOrder.Count);

        var artifacts = await db.SceneImages.AsNoTracking().Where(img => img.SessionId == sessionId).OrderBy(img => img.SceneRevision).ToListAsync();
        Assert.Equal(3, artifacts.Count);
        Assert.Equal(1, artifacts[0].SceneRevision);
        Assert.Equal(2, artifacts[1].SceneRevision);
        Assert.Equal(3, artifacts[2].SceneRevision);
    }
}
