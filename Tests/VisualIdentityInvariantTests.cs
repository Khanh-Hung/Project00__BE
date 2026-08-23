using System.Text.Json;
using Application.DTOs;
using Application.Exceptions;
using Application.Interfaces;
using Application.Services;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.ImageGeneration;
using Infrastructure.ImageGeneration.ComfyUI;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests;

public sealed class VisualIdentityInvariantTests
{
    private sealed class CountingImageService : IImageGenerationService
    {
        public int CallCount { get; private set; } = 0;
        public ImageGenerationRequest? LastRequest { get; private set; }

        public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default)
            => Task.FromResult("https://images.storage/dummy.png");

        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default)
            => Task.FromResult("https://images.storage/dummy.png");

        public Task<ImageGenerationResult> GenerateImageWithResultAsync(ImageGenerationRequest request, CancellationToken ct = default)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(new ImageGenerationResult(
                ImageUrl: $"https://images.storage/generated_{CallCount}.png",
                Provider: "ComfyUI",
                ProviderJobId: $"prompt_{CallCount}",
                DurationMs: 120,
                Seed: request.Seed ?? 12345
            ));
        }
    }

    private sealed class MockComfyUIClient : IComfyUIClient
    {
        public int QueuePromptCallCount { get; private set; } = 0;
        public int GetHistoryCallCount { get; private set; } = 0;
        public string? LastPolledPromptId { get; private set; }

        public Task<string> QueuePromptAsync(Dictionary<string, object> workflowGraph, CancellationToken ct = default)
        {
            QueuePromptCallCount++;
            return Task.FromResult("prompt-123");
        }

        public Task<ComfyUIHistoryResult?> GetHistoryAsync(string promptId, CancellationToken ct = default)
        {
            GetHistoryCallCount++;
            LastPolledPromptId = promptId;
            return Task.FromResult<ComfyUIHistoryResult?>(new ComfyUIHistoryResult(
                PromptId: promptId,
                IsSuccess: true,
                ErrorMessage: null,
                OutputImages: new List<ComfyUIHistoryOutputImage>
                {
                    new ComfyUIHistoryOutputImage("rendered_test.png", "", "output")
                }
            ));
        }

        public Task<byte[]> DownloadImageAsync(string filename, string? subfolder = null, string? type = null, CancellationToken ct = default)
        {
            return Task.FromResult(new byte[] { 1, 2, 3, 4 });
        }
    }

    private sealed class MockInputImageService : IComfyUIInputImageService
    {
        public Task<string> EnsureImageUploadedAsync(string? referenceImageUrl, CancellationToken ct = default)
            => Task.FromResult("canonical_face.png");
    }

    private static (ProjectDbContext db, ImageGenerationJobHandler handler, CountingImageService imageService) CreateHarness(string dbName)
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        var db = new ProjectDbContext(options);
        var imageService = new CountingImageService();
        var visualCompiler = new VisualPromptCompiler();
        var handler = new ImageGenerationJobHandler(db, visualCompiler, imageService, NullLogger<ImageGenerationJobHandler>.Instance, new SystemDateTimeProvider());
        return (db, handler, imageService);
    }

    private static VisualSnapshot CreateTestSnapshot(Guid sessionId, Guid turnId, int revision = 1, string? canonicalRef = "https://cloud.storage/canonical_face.png", string? parametersJson = "{\"ipAdapter\":{\"weight\":0.45,\"endAt\":0.70}}")
    {
        var profile = GenerationProfile.CreateDefault(
            workflow: "VisualIdentity",
            workflowVersion: 1,
            parametersJson: parametersJson
        );

        return VisualSnapshot.Create(
            turnId: turnId,
            sessionId: sessionId,
            characterId: Guid.NewGuid(),
            sceneRevision: revision,
            visualIdentity: new CharacterVisualIdentity(
                Hair: "Silver long hair",
                Eyes: "Crimson red eyes",
                CanonicalReferenceUrl: canonicalRef
            ),
            sceneState: new SessionSceneState(
                CurrentLocation: "Sanctuary",
                CurrentPosition: "Altar",
                CurrentOutfit: "White Gown",
                CurrentTimeOfDay: "Night",
                HeldItems: null,
                Atmosphere: "Mystical",
                SceneRevision: revision,
                LastUpdatedAt: Clock.Now
            ),
            transientState: new TransientVisualState(
                Action: "Standing gracefully",
                Pose: "Elegant posture",
                Expression: "Gentle smile"
            ),
            generationProfile: profile
        );
    }

    [Fact]
    public async Task Invariant1_SameGenerationRequest_DoesNotInvokeGpuTwice()
    {
        var dbName = Guid.NewGuid().ToString();
        var (db, handler, imageService) = CreateHarness(dbName);

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var snapshot = CreateTestSnapshot(sessionId, turnId, revision: 1);

        var payload = new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, requestId);

        // First Execution -> Calls GPU once
        var res1 = await handler.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-1", Clock.Now);
        Assert.Equal(JobExecutionStatus.Completed, res1.Status);
        Assert.Equal(1, imageService.CallCount);

        // Second Execution (same GenerationRequestId e.g. outbox redelivery) -> Fast Skipped, zero GPU invocation
        var res2 = await handler.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-1", Clock.Now);
        Assert.Equal(JobExecutionStatus.Skipped, res2.Status);
        Assert.Equal(1, imageService.CallCount);
    }

    [Fact]
    public async Task Invariant2_RegenerateSameTurn_CreatesNewGeneration_AndUpdatesIsCurrent()
    {
        var dbName = Guid.NewGuid().ToString();
        var (db, handler, imageService) = CreateHarness(dbName);

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var snapshot = CreateTestSnapshot(sessionId, turnId, revision: 1);

        // Attempt 1: Generation Request A
        var requestIdA = Guid.NewGuid();
        var payloadA = new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, requestIdA);
        var resA = await handler.HandleSceneImageGenerationAsync(payloadA, Guid.NewGuid(), "worker-1", Clock.Now);
        Assert.Equal(JobExecutionStatus.Completed, resA.Status);

        var artifactA = await db.SceneImages.FirstAsync(img => img.GenerationRequestId == requestIdA);
        Assert.True(artifactA.IsCurrent);

        // Attempt 2: User clicks Regenerate for same Turn / Revision -> Generation Request B
        var requestIdB = Guid.NewGuid();
        var payloadB = new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, requestIdB);
        var resB = await handler.HandleSceneImageGenerationAsync(payloadB, Guid.NewGuid(), "worker-1", Clock.Now);
        Assert.Equal(JobExecutionStatus.Completed, resB.Status);

        // Both artifacts exist in DB, artifact B is current, artifact A is no longer current
        var allArtifacts = await db.SceneImages.Where(img => img.SessionId == sessionId && img.SceneRevision == 1).ToListAsync();
        Assert.Equal(2, allArtifacts.Count);

        var updatedA = allArtifacts.First(img => img.GenerationRequestId == requestIdA);
        var updatedB = allArtifacts.First(img => img.GenerationRequestId == requestIdB);

        Assert.False(updatedA.IsCurrent);
        Assert.True(updatedB.IsCurrent);
    }

    [Fact]
    public async Task Invariant3_ConcurrentWorkers_OnlyOneOwnsLease()
    {
        var dbName = Guid.NewGuid().ToString();
        var (db, handler, _) = CreateHarness(dbName);

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var now = Clock.Now;

        // Seed a Job with active lease owned by Worker A until now + 2 min
        var job = new ImageGenerationJob(sessionId, turnId, Guid.NewGuid(), 1, requestId);
        job.TryClaim("worker-A", TimeSpan.FromMinutes(2), now);
        await db.ImageGenerationJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var snapshot = CreateTestSnapshot(sessionId, turnId, revision: 1);
        var payload = new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, requestId);

        // Worker B attempts to run -> Deferred because Worker A owns active lease
        var resB = await handler.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-B", now);
        Assert.Equal(JobExecutionStatus.Deferred, resB.Status);
    }

    [Fact]
    public async Task Invariant4_StaleProcessingJob_CanBeReclaimed_ByNewWorker()
    {
        var dbName = Guid.NewGuid().ToString();
        var (db, handler, imageService) = CreateHarness(dbName);

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var crashTime = Clock.Now.AddMinutes(-5);

        // Seed a Job that crashed 5 minutes ago (lease was for 2 mins, so it expired 3 mins ago)
        var job = new ImageGenerationJob(sessionId, turnId, Guid.NewGuid(), 1, requestId);
        job.TryClaim("worker-dead", TimeSpan.FromMinutes(2), crashTime);
        await db.ImageGenerationJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var snapshot = CreateTestSnapshot(sessionId, turnId, revision: 1);
        var payload = new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, requestId);

        // Worker B arrives now -> Reclaims stale job successfully
        var resB = await handler.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-alive", Clock.Now);
        Assert.Equal(JobExecutionStatus.Completed, resB.Status);
        Assert.Equal(1, imageService.CallCount);

        var updatedJob = await db.ImageGenerationJobs.FirstAsync(j => j.GenerationRequestId == requestId);
        Assert.Equal(ImageJobStatus.Completed, updatedJob.Status);
    }

    [Fact]
    public async Task Invariant5_WorkflowVersionMismatch_FailsWithoutFallback()
    {
        var config = new ConfigurationBuilder().Build();
        using var httpClient = new HttpClient();
        var storage = new ComfyUIImageGenerationIntegrationTests_InMemoryStorageService();
        var inputService = new ComfyUIInputImageService(httpClient, config, NullLogger<ComfyUIInputImageService>.Instance);
        var comfyClient = new ComfyUIClient(httpClient, config, NullLogger<ComfyUIClient>.Instance);

        // Server only has Workflow V1 registered
        var workflowBuilders = new IComfyUIWorkflowBuilder[] { new VisualIdentityWorkflowV1Builder() };
        var service = new ComfyUIImageGenerationService(comfyClient, storage, inputService, workflowBuilders, config, NullLogger<ComfyUIImageGenerationService>.Instance);

        // Request demands VisualIdentity V99
        var request = new ImageGenerationRequest(
            Prompt: "solo, 1girl",
            Workflow: "VisualIdentity",
            WorkflowVersion: 99,
            ReferenceImageUrl: "https://cloud.storage/canonical.png"
        );

        // Must fail with GpuNonTransientException, NOT silently fall back to V1!
        await Assert.ThrowsAsync<GpuNonTransientException>(() => service.GenerateImageWithResultAsync(request));
    }

    [Fact]
    public async Task Invariant6_PredecessorCompletesAfterSnapshotCreation_UsesPredecessorArtifact()
    {
        var dbName = Guid.NewGuid().ToString();
        var (db, handler, imageService) = CreateHarness(dbName);

        var sessionId = Guid.NewGuid();
        var turn1Id = Guid.NewGuid();
        var turn2Id = Guid.NewGuid();

        // Step 1: Turn 1 completes and generates image 1
        var snapshot1 = CreateTestSnapshot(sessionId, turn1Id, revision: 1);
        var req1 = new SceneImageGenerationOutboxPayload(turn1Id, snapshot1.CharacterId, Guid.NewGuid(), snapshot1, Guid.NewGuid());
        await handler.HandleSceneImageGenerationAsync(req1, Guid.NewGuid(), "worker-1", Clock.Now);

        var turn1Artifact = await db.SceneImages.FirstAsync(img => img.SessionId == sessionId && img.SceneRevision == 1);
        Assert.Equal("https://images.storage/generated_1.png", turn1Artifact.ImageUrl);

        // Step 2: Turn 2 snapshot was created with PreviousSceneImageUrl = null
        var snapshot2 = CreateTestSnapshot(sessionId, turn2Id, revision: 2);

        var req2 = new SceneImageGenerationOutboxPayload(turn2Id, snapshot2.CharacterId, Guid.NewGuid(), snapshot2, Guid.NewGuid());
        await handler.HandleSceneImageGenerationAsync(req2, Guid.NewGuid(), "worker-1", Clock.Now);

        // Step 3: Worker must late-resolve PreviousSceneImageUrl from Turn 1's artifact!
        Assert.Equal("https://images.storage/generated_1.png", imageService.LastRequest?.PreviousSceneImageUrl);
    }

    [Fact]
    public async Task Invariant7_UserRequestsBlackOutfit_IsNotBlockedByIdentityNegativePrompt()
    {
        var visualCompiler = new VisualPromptCompiler();
        var snapshot = VisualSnapshot.Create(
            turnId: Guid.NewGuid(),
            sessionId: Guid.NewGuid(),
            characterId: Guid.NewGuid(),
            sceneRevision: 1,
            visualIdentity: new CharacterVisualIdentity(
                Hair: "Silver long straight hair",
                Eyes: "Crimson red eyes",
                ClothingStyle: "Black evening gown, elegant dark velvet dress"
            ),
            sceneState: new SessionSceneState(
                CurrentLocation: "Grand Ballroom",
                CurrentPosition: "Balcony",
                CurrentOutfit: "Black evening gown, dark crimson silk dress",
                CurrentTimeOfDay: "Night",
                HeldItems: null,
                Atmosphere: "Romantic",
                SceneRevision: 1,
                LastUpdatedAt: Clock.Now
            ),
            transientState: new TransientVisualState(Action: "Holding wine glass", Pose: "Standing", Expression: "Charming smile"),
            generationProfile: GenerationProfile.CreateDefault()
        );

        var compiledPositive = visualCompiler.CompileScenePrompt(snapshot);
        var workflowBuilder = new VisualIdentityWorkflowV1Builder();

        var request = ImageGenerationRequest.FromSnapshot(snapshot, compiledPositive);
        var workflow = workflowBuilder.BuildWorkflow(request, "canonical_face_ref.png");

        var node6 = (Dictionary<string, object>)workflow["6"]; // Positive
        var node7 = (Dictionary<string, object>)workflow["7"]; // Negative

        var posInputs = (Dictionary<string, object>)node6["inputs"];
        var negInputs = (Dictionary<string, object>)node7["inputs"];

        var posText = (string)posInputs["text"];
        var negText = (string)negInputs["text"];

        // Positive prompt contains user outfit request
        Assert.Contains("Black evening gown", posText, StringComparison.OrdinalIgnoreCase);

        // Negative prompt MUST NOT ban black or dark clothing!
        Assert.DoesNotContain("black clothing", negText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("black dress", negText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dark clothing", negText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no horns", negText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrencyTestA_TwoWorkersClaimSameExistingJob_OnlyOneExecutesGpu()
    {
        var dbName = Guid.NewGuid().ToString();
        var (db, handlerA, imageService) = CreateHarness(dbName);
        var visualCompiler = new VisualPromptCompiler();
        var handlerB = new ImageGenerationJobHandler(db, visualCompiler, imageService, NullLogger<ImageGenerationJobHandler>.Instance, new SystemDateTimeProvider());

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var snapshot = CreateTestSnapshot(sessionId, turnId, revision: 1);
        var payload = new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, requestId);

        // Run both workers concurrently
        var taskA = handlerA.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-A", Clock.Now);
        var taskB = handlerB.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-B", Clock.Now);

        var results = await Task.WhenAll(taskA, taskB);

        // Exactly one worker completes, one worker is deferred or skipped
        var completedCount = results.Count(r => r.Status == JobExecutionStatus.Completed);
        Assert.Equal(1, completedCount);
        Assert.Equal(1, imageService.CallCount);

        var images = await db.SceneImages.Where(img => img.SessionId == sessionId).ToListAsync();
        Assert.Single(images);
    }

    private sealed class BarrierImageService : IImageGenerationService
    {
        public TaskCompletionSource<bool> WorkerAStartedTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> WorkerAReleaseTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; } = 0;

        public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default)
            => Task.FromResult("https://images.storage/dummy.png");

        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default)
            => Task.FromResult("https://images.storage/dummy.png");

        public async Task<ImageGenerationResult> GenerateImageWithResultAsync(ImageGenerationRequest request, CancellationToken ct = default)
        {
            CallCount++;
            if (CallCount == 1)
            {
                WorkerAStartedTcs.TrySetResult(true);
                await WorkerAReleaseTcs.Task;
            }
            return new ImageGenerationResult(
                ImageUrl: $"https://images.storage/generated_{CallCount}.png",
                Provider: "ComfyUI",
                ProviderJobId: $"prompt_{CallCount}",
                DurationMs: 120,
                Seed: request.Seed ?? 12345
            );
        }
    }

    [Fact]
    public async Task ConcurrencyTestB_ExpiredLeaseRace_StaleWorkerCannotOverwriteNewOwner()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        var dbA = new ProjectDbContext(options);
        var dbB = new ProjectDbContext(options);

        var barrierService = new BarrierImageService();
        var visualCompiler = new VisualPromptCompiler();
        var handlerA = new ImageGenerationJobHandler(dbA, visualCompiler, barrierService, NullLogger<ImageGenerationJobHandler>.Instance, new SystemDateTimeProvider());
        var handlerB = new ImageGenerationJobHandler(dbB, visualCompiler, barrierService, NullLogger<ImageGenerationJobHandler>.Instance, new SystemDateTimeProvider());

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var snapshot = CreateTestSnapshot(sessionId, turnId, revision: 1);
        var payload = new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, requestId);

        var startTime = Clock.Now;

        // 1. Worker A claims job at startTime (lease 4 mins) and begins GPU generation (paused at barrier)
        var workerATask = Task.Run(() => handlerA.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-A", startTime));

        // 2. Wait until Worker A is actively running GPU
        await barrierService.WorkerAStartedTcs.Task;

        // 3. Time advances by 5 minutes (Worker A's lease expires!)
        var reclaimTime = startTime.AddMinutes(5);

        // 4. Worker B arrives, reclaims stale job, executes GPU, and completes!
        var resB = await handlerB.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-B", reclaimTime);
        Assert.Equal(JobExecutionStatus.Completed, resB.Status);

        // 5. Worker A finishes GPU and attempts to commit artifact
        barrierService.WorkerAReleaseTcs.TrySetResult(true);
        var resA = await workerATask;

        // 6. Assertions:
        // Worker A MUST BE DISCARDED (Deferred) because it lost ownership!
        Assert.Equal(JobExecutionStatus.Deferred, resA.Status);

        // Exactly ONE SceneImage artifact exists in DB (committed by Worker B)
        var allImages = await dbB.SceneImages.Where(img => img.SessionId == sessionId).ToListAsync();
        Assert.Single(allImages);
        Assert.True(allImages[0].IsCurrent);

        // The job in DB is completed by Worker B
        var jobInDb = await dbB.ImageGenerationJobs.FirstAsync(j => j.GenerationRequestId == requestId);
        Assert.Equal("worker-B", jobInDb.ClaimedBy);
        Assert.Equal(ImageJobStatus.Completed, jobInDb.Status);
    }

    private sealed class TestDateTimeProvider : IDateTimeProvider
    {
        public DateTime CurrentTime { get; set; }
        public DateTime UtcNow => CurrentTime;

        public TestDateTimeProvider(DateTime initialTime)
        {
            CurrentTime = initialTime;
        }

        public void Advance(TimeSpan delta)
        {
            CurrentTime = CurrentTime.Add(delta);
        }
    }

    private sealed class ActionImageService : IImageGenerationService
    {
        private readonly Action _onGenerate;

        public ActionImageService(Action onGenerate)
        {
            _onGenerate = onGenerate;
        }

        public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default)
            => Task.FromResult("https://images.storage/dummy.png");
        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default)
            => Task.FromResult("https://images.storage/dummy.png");

        public Task<ImageGenerationResult> GenerateImageWithResultAsync(ImageGenerationRequest request, CancellationToken ct = default)
        {
            _onGenerate();
            return Task.FromResult(new ImageGenerationResult(
                ImageUrl: "https://images.storage/rendered.png",
                Provider: "ComfyUI",
                ProviderJobId: "prompt_done",
                DurationMs: 100,
                Seed: request.Seed ?? 12345
            ));
        }
    }

    private sealed class CancellingImageService : IImageGenerationService
    {
        private readonly CancellationTokenSource _cts;

        public CancellingImageService(CancellationTokenSource cts)
        {
            _cts = cts;
        }

        public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default)
            => Task.FromResult("https://images.storage/dummy.png");
        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default)
            => Task.FromResult("https://images.storage/dummy.png");

        public Task<ImageGenerationResult> GenerateImageWithResultAsync(ImageGenerationRequest request, CancellationToken ct = default)
        {
            _cts.Cancel();
            throw new OperationCanceledException(_cts.Token);
        }
    }

    [Fact]
    public async Task ConcurrencyTestC_LeaseExpiresWithNoOtherWorker_StaleWorkerCannotCommitArtifact()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        var db = new ProjectDbContext(options);

        var startTime = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestDateTimeProvider(startTime);

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        var timeAdvancingService = new ActionImageService(() =>
        {
            // GPU runs for 5 minutes -> time advances past 4-minute lease!
            timeProvider.Advance(TimeSpan.FromMinutes(5));
        });

        var visualCompiler = new VisualPromptCompiler();
        var handler = new ImageGenerationJobHandler(db, visualCompiler, timeAdvancingService, NullLogger<ImageGenerationJobHandler>.Instance, timeProvider);

        var snapshot = CreateTestSnapshot(sessionId, turnId, revision: 1);
        var payload = new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, requestId);

        // Worker A claims job at T0 (lease is T0 + 4 mins)
        // During GPU execution, time advances to T0 + 5 mins (lease expired!)
        // Worker A completes GPU and attempts to commit artifact
        // Handler checks timeProvider.UtcNow (T0 + 5 mins) > LeaseUntil (T0 + 4 mins) -> Discarded with Deferred!
        var resA = await handler.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-A", startTime);
        Assert.Equal(JobExecutionStatus.Deferred, resA.Status);

        // ZERO SceneImages committed to database!
        var allImages = await db.SceneImages.Where(img => img.SessionId == sessionId).ToListAsync();
        Assert.Empty(allImages);
    }

    [Fact]
    public async Task ConcurrencyTestD_HostCancellationInterruption_LeavesJobRecoverableUponRestart()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        var db = new ProjectDbContext(options);

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var snapshot = CreateTestSnapshot(sessionId, turnId, revision: 1);
        var payload = new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, requestId);

        using var cts = new CancellationTokenSource();
        var cancellingService = new CancellingImageService(cts);
        var visualCompiler = new VisualPromptCompiler();
        var handlerA = new ImageGenerationJobHandler(db, visualCompiler, cancellingService, NullLogger<ImageGenerationJobHandler>.Instance, new SystemDateTimeProvider());

        // 1. Worker A interrupted by host shutdown / cancellation during GPU execution
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handlerA.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-A", Clock.Now, cts.Token));

        // Job in DB must NOT be permanently marked Cancelled!
        var jobInDb = await db.ImageGenerationJobs.FirstAsync(j => j.GenerationRequestId == requestId);
        Assert.NotEqual(ImageJobStatus.Cancelled, jobInDb.Status);
        Assert.Equal(ImageJobStatus.Processing, jobInDb.Status);

        // 2. Restart: Worker B arrives after lease expired -> Successfully reclaims and completes!
        var countingService = new CountingImageService();
        var handlerB = new ImageGenerationJobHandler(db, visualCompiler, countingService, NullLogger<ImageGenerationJobHandler>.Instance, new SystemDateTimeProvider());
        var resRestart = await handlerB.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-B", Clock.Now.AddMinutes(5));
        Assert.Equal(JobExecutionStatus.Completed, resRestart.Status);
        Assert.Equal(1, countingService.CallCount);

        var finalJob = await db.ImageGenerationJobs.FirstAsync(j => j.GenerationRequestId == requestId);
        Assert.Equal(ImageJobStatus.Completed, finalJob.Status);
    }

    private sealed class TwoWorkerBarrierImageService : IImageGenerationService
    {
        private readonly CountdownEvent _barrier = new CountdownEvent(2);
        private readonly TaskCompletionSource<bool> _releaseTcs = new TaskCompletionSource<bool>();

        public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default)
            => Task.FromResult("https://images.storage/dummy.png");
        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default)
            => Task.FromResult("https://images.storage/dummy.png");

        public async Task<ImageGenerationResult> GenerateImageWithResultAsync(ImageGenerationRequest request, CancellationToken ct = default)
        {
            _barrier.Signal();
            if (_barrier.IsSet)
            {
                _releaseTcs.TrySetResult(true);
            }
            await _releaseTcs.Task;

            return new ImageGenerationResult(
                ImageUrl: $"https://images.storage/{Guid.NewGuid()}.png",
                Provider: "ComfyUI",
                ProviderJobId: "prompt_race",
                DurationMs: 100,
                Seed: request.Seed ?? 12345
            );
        }
    }

    [Fact]
    public async Task ConcurrencyTest_ConcurrentRegenerations_RealRaceWithBarrier_GuaranteesAtMostOneCurrentArtifact()
    {
        var connectionString = $"Data Source=SharedDb_{Guid.NewGuid()};Mode=Memory;Cache=Shared";
        using var masterConnection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await masterConnection.OpenAsync();

        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connectionString)
            .Options;

        using (var setupDb = new ProjectDbContext(options))
        {
            await setupDb.Database.EnsureCreatedAsync();
        }

        var db1 = new ProjectDbContext(options);
        var db2 = new ProjectDbContext(options);

        var barrierService = new TwoWorkerBarrierImageService();
        var visualCompiler = new VisualPromptCompiler();
        var handler1 = new ImageGenerationJobHandler(db1, visualCompiler, barrierService, NullLogger<ImageGenerationJobHandler>.Instance, new SystemDateTimeProvider());
        var handler2 = new ImageGenerationJobHandler(db2, visualCompiler, barrierService, NullLogger<ImageGenerationJobHandler>.Instance, new SystemDateTimeProvider());

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var snapshot = CreateTestSnapshot(sessionId, turnId, revision: 1);

        var req1 = Guid.NewGuid();
        var payload1 = new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, req1);
        var req2 = Guid.NewGuid();
        var payload2 = new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, req2);

        // Run both workers concurrently racing to commit revision 1!
        var task1 = Task.Run(() => handler1.HandleSceneImageGenerationAsync(payload1, Guid.NewGuid(), "worker-1", Clock.Now));
        var task2 = Task.Run(() => handler2.HandleSceneImageGenerationAsync(payload2, Guid.NewGuid(), "worker-2", Clock.Now));

        var results = await Task.WhenAll(task1, task2);
        Assert.Contains(results, r => r.Status == JobExecutionStatus.Completed);

        // Assert DB guarantees EXACTLY ONE artifact has IsCurrent = true!
        using var verifyDb = new ProjectDbContext(options);
        var allImages = await verifyDb.SceneImages.AsNoTracking().Where(img => img.SessionId == sessionId && img.SceneRevision == 1).ToListAsync();
        Assert.NotEmpty(allImages);

        var currentImages = allImages.Where(img => img.IsCurrent).ToList();
        Assert.Single(currentImages);
    }

    [Fact]
    public async Task RelationalAtomicFencingAndTransactionTest_CommitSucceedsWithExactOneCurrentArtifact()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connection)
            .Options;

        using var db = new ProjectDbContext(options);
        await db.Database.EnsureCreatedAsync();

        Assert.True(db.Database.IsRelational());

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var snapshot = CreateTestSnapshot(sessionId, turnId, revision: 1);
        var payload = new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, requestId);

        var countingService = new CountingImageService();
        var visualCompiler = new VisualPromptCompiler();
        var handler = new ImageGenerationJobHandler(db, visualCompiler, countingService, NullLogger<ImageGenerationJobHandler>.Instance, new SystemDateTimeProvider());

        // Worker executes relational path: ExecuteUpdateAsync + BeginTransactionAsync
        var res = await handler.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "relational-worker", Clock.Now);
        Assert.Equal(JobExecutionStatus.Completed, res.Status);

        // Assert job completed and artifact committed atomically
        using var verifyDb = new ProjectDbContext(options);
        var jobInDb = await verifyDb.ImageGenerationJobs.AsNoTracking().FirstAsync(j => j.GenerationRequestId == requestId);
        Assert.Equal(ImageJobStatus.Completed, jobInDb.Status);

        var artifacts = await verifyDb.SceneImages.AsNoTracking().Where(img => img.SessionId == sessionId).ToListAsync();
        Assert.Single(artifacts);
        Assert.True(artifacts[0].IsCurrent);
    }

    [Fact]
    public async Task RelationalAtomicFencingAndTransactionTest_StaleFencingRollsBackAndInsertsNoArtifact()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(connection)
            .Options;

        using var db = new ProjectDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var startTime = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestDateTimeProvider(startTime);

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var snapshot = CreateTestSnapshot(sessionId, turnId, revision: 1);
        var payload = new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, requestId);

        var timeAdvancingService = new ActionImageService(() =>
        {
            // Advance time past 4-minute lease
            timeProvider.Advance(TimeSpan.FromMinutes(5));
        });

        var visualCompiler = new VisualPromptCompiler();
        var handler = new ImageGenerationJobHandler(db, visualCompiler, timeAdvancingService, NullLogger<ImageGenerationJobHandler>.Instance, timeProvider);

        // Worker A attempts commit after lease expired -> relational ExecuteUpdateAsync rowsAffected == 0 -> Rollback
        var res = await handler.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "relational-worker-A", startTime);
        Assert.Equal(JobExecutionStatus.Deferred, res.Status);

        // ZERO SceneImages committed!
        var artifacts = await db.SceneImages.Where(img => img.SessionId == sessionId).ToListAsync();
        Assert.Empty(artifacts);
    }

    [Fact]
    public async Task BoundaryTest_CrashAfterQueuePromptBeforeProviderJobIdSaved_ReExecutesPromptAndCommitsSingleWinningArtifact()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        var db = new ProjectDbContext(options);

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var snapshot = CreateTestSnapshot(sessionId, turnId, revision: 1);
        var payload = new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, requestId);

        var mockComfyClient = new MockComfyUIClient();
        var config = new ConfigurationBuilder().Build();
        var storage = new ComfyUIImageGenerationIntegrationTests_InMemoryStorageService();
        var inputService = new MockInputImageService();
        var workflowBuilders = new IComfyUIWorkflowBuilder[] { new VisualIdentityWorkflowV1Builder() };
        var comfyService = new ComfyUIImageGenerationService(mockComfyClient, storage, inputService, workflowBuilders, config, NullLogger<ComfyUIImageGenerationService>.Instance);

        var visualCompiler = new VisualPromptCompiler();
        var handler = new ImageGenerationJobHandler(db, visualCompiler, comfyService, NullLogger<ImageGenerationJobHandler>.Instance, new SystemDateTimeProvider());

        // 1. First execution: ComfyUI receives /prompt, but worker crashes before ProviderJobId is saved to DB
        var requestCrash = ImageGenerationRequest.FromSnapshot(
            snapshot: snapshot,
            compiledPrompt: "1girl",
            providerJobId: null,
            onPromptQueuedAsync: (promptId, ct) => throw new InvalidOperationException("Process crashed before saving ProviderJobId!")
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() => comfyService.GenerateImageWithResultAsync(requestCrash));
        Assert.Equal(1, mockComfyClient.QueuePromptCallCount);

        // 2. Recovery execution: Worker restarts with ProviderJobId = null, calls QueuePromptAsync again (at-least-once external generation)
        var resRecovery = await handler.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-recovery", Clock.Now);
        Assert.Equal(JobExecutionStatus.Completed, resRecovery.Status);
        Assert.Equal(2, mockComfyClient.QueuePromptCallCount); // External ComfyUI called twice due to crash window

        // 3. Assert DB guarantees strictly exactly-once artifact persistence
        var committedImages = await db.SceneImages.Where(img => img.SessionId == sessionId).ToListAsync();
        Assert.Single(committedImages);
        Assert.Equal(requestId, committedImages[0].GenerationRequestId);
    }

    [Fact]
    public async Task BoundaryTestC_CrashAfterQueuePrompt_ResumesPollingExistingProviderJobId_WithoutSecondQueuePrompt()
    {
        var config = new ConfigurationBuilder().Build();
        var mockComfyClient = new MockComfyUIClient();
        var storage = new ComfyUIImageGenerationIntegrationTests_InMemoryStorageService();
        var inputService = new ComfyUIInputImageService(new HttpClient(), config, NullLogger<ComfyUIInputImageService>.Instance);
        var workflowBuilders = new IComfyUIWorkflowBuilder[] { new VisualIdentityWorkflowV1Builder() };

        var service = new ComfyUIImageGenerationService(mockComfyClient, storage, inputService, workflowBuilders, config, NullLogger<ComfyUIImageGenerationService>.Instance);

        // Request with pre-existing ProviderJobId="prompt-recovered" (simulating recovery after crash)
        var request = new ImageGenerationRequest(
            Prompt: "1girl, solo",
            ReferenceImageUrl: "https://cloud.storage/canonical.png",
            ProviderJobId: "prompt-recovered",
            Seed: 12345
        );

        var result = await service.GenerateImageWithResultAsync(request);

        // Must NOT call QueuePromptAsync!
        Assert.Equal(0, mockComfyClient.QueuePromptCallCount);
        // Must call GetHistoryAsync with "prompt-recovered"
        Assert.Equal(1, mockComfyClient.GetHistoryCallCount);
        Assert.Equal("prompt-recovered", mockComfyClient.LastPolledPromptId);
        Assert.Equal("prompt-recovered", result.ProviderJobId);
    }

    [Fact]
    public async Task ValidationTestD_MalformedParametersJson_ThrowsGpuNonTransientException_NoGpuCall()
    {
        var builder = new VisualIdentityWorkflowV1Builder();
        var request = new ImageGenerationRequest(
            Prompt: "1girl",
            ParametersJson: "{ malformed json...",
            Seed: 12345
        );

        // Must throw GpuNonTransientException fail-fast
        await Assert.ThrowsAsync<GpuNonTransientException>(() =>
        {
            builder.BuildWorkflow(request, "canonical_face.png");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ValidationTestE_MissingCanonicalReferenceUrl_ThrowsGpuNonTransientException_NoGpuCall()
    {
        var builder = new VisualIdentityWorkflowV1Builder();
        var request = new ImageGenerationRequest(
            Prompt: "1girl",
            ReferenceImageUrl: null,
            Seed: 12345
        );

        // Must throw GpuNonTransientException when resolvedReferenceImageName is empty
        await Assert.ThrowsAsync<GpuNonTransientException>(() =>
        {
            builder.BuildWorkflow(request, string.Empty);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ValidationTestH_MissingSeed_ThrowsGpuNonTransientException_StrictDeterminism()
    {
        var builder = new VisualIdentityWorkflowV1Builder();
        var request = new ImageGenerationRequest(
            Prompt: "1girl",
            Seed: null // Missing Seed
        );

        // Must throw GpuNonTransientException to enforce strict determinism
        await Assert.ThrowsAsync<GpuNonTransientException>(() =>
        {
            builder.BuildWorkflow(request, "canonical_face.png");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void DbExceptionClassifier_CorrectlyDistinguishes_TransientVsPermanentExceptions()
    {
        // Typed transient errors
        Assert.True(DbExceptionClassifier.IsTransient(new DbUpdateConcurrencyException("Concurrency conflict")));
        Assert.True(DbExceptionClassifier.IsTransient(new TimeoutException("DB timeout")));
        Assert.True(DbExceptionClassifier.IsTransient(new System.Net.Sockets.SocketException()));
        Assert.True(DbExceptionClassifier.IsTransient(new HttpRequestException("Network drop")));

        // Permanent domain / argument errors
        Assert.False(DbExceptionClassifier.IsTransient(new ArgumentException("Invalid arguments")));
        Assert.False(DbExceptionClassifier.IsTransient(new InvalidOperationException("Schema corruption")));
        Assert.False(DbExceptionClassifier.IsTransient(new NullReferenceException()));
    }

    [Fact]
    public void DbExceptionClassifier_FailClosed_MisleadingMessageDoesNotMakeUnknownExceptionTransient()
    {
        // Generic exceptions with words like 'storage', 'temporary', 'transient' MUST fail-closed to false
        Assert.False(DbExceptionClassifier.IsTransient(new InvalidOperationException("Permanent storage configuration is invalid")));
        Assert.False(DbExceptionClassifier.IsTransient(new ArgumentException("Temporary directory path is invalid")));
        Assert.False(DbExceptionClassifier.IsTransient(new Exception("Database network error occurred")));
        Assert.False(DbExceptionClassifier.IsTransient(new Exception("Transient state timeout")));
    }

    [Fact]
    public async Task VisualGenerationProfileProvider_ZeroStateDrift_ConfigurationMutationAfterSnapshot_DoesNotMutateFrozenSnapshot()
    {
        // 1. Initial configuration: Weight = 0.45, EndAt = 0.70
        var configDict = new Dictionary<string, string?>
        {
            ["AiProviders:ImageGeneration:DefaultWorkflow"] = "VisualIdentity",
            ["AiProviders:ImageGeneration:DefaultWorkflowVersion"] = "1",
            ["AiProviders:ImageGeneration:IPAdapter:Weight"] = "0.45",
            ["AiProviders:ImageGeneration:IPAdapter:EndAt"] = "0.70"
        };
        var configSource = new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource { InitialData = configDict };
        var configRoot = new ConfigurationBuilder().Add(configSource).Build();

        var profileProvider = new VisualGenerationProfileProvider(configRoot);
        var resolver = new VisualStateResolver(
            unitOfWork: null!,
            sceneStateTracker: null,
            profileProvider: profileProvider,
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<VisualStateResolver>.Instance
        );

        var visualIdentity = new CharacterVisualIdentity(
            Face: "delicate youthful features",
            Hair: "long silver hair",
            Eyes: "blue eyes",
            Skin: "fair skin",
            Body: "slender",
            AgeAppearance: "early 20s",
            ClothingStyle: "robe",
            Accessories: null,
            VisualTraits: null,
            CanonicalReferenceUrl: "canonical.png"
        );
        var character = new Character("Aria", "Mage", "https://example.com/avatar.jpg", "Friendly", "Hello", "Fantasy", visualIdentity: visualIdentity);
        var session = new ChatSession(character.Id, Guid.NewGuid(), "Chat with Aria");

        // 2. Turn 1 Snapshot created under initial configuration
        var (_, _, snapshotTurn1) = await resolver.ResolveTurnVisualStateAsync(
            character: character,
            session: session,
            userMessage: "Turn 1 message",
            assistantReply: "Turn 1 reply",
            currentMood: CharacterMood.Neutral,
            turnId: Guid.NewGuid()
        );

        Assert.Contains("\"weight\":0.45", snapshotTurn1.GenerationProfile.ParametersJson);
        Assert.Contains("\"endAt\":0.70", snapshotTurn1.GenerationProfile.ParametersJson);

        // 3. Mutate runtime configuration (e.g. administrator reconfigures weights for Turn 2)
        var newConfigDict = new Dictionary<string, string?>
        {
            ["AiProviders:ImageGeneration:DefaultWorkflow"] = "VisualIdentity",
            ["AiProviders:ImageGeneration:DefaultWorkflowVersion"] = "1",
            ["AiProviders:ImageGeneration:IPAdapter:Weight"] = "0.20",
            ["AiProviders:ImageGeneration:IPAdapter:EndAt"] = "0.30"
        };
        var newConfigRoot = new ConfigurationBuilder().AddInMemoryCollection(newConfigDict).Build();
        var newProfileProvider = new VisualGenerationProfileProvider(newConfigRoot);
        var newResolver = new VisualStateResolver(
            unitOfWork: null!,
            sceneStateTracker: null,
            profileProvider: newProfileProvider,
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<VisualStateResolver>.Instance
        );

        // Turn 2 Snapshot created under mutated configuration
        var (_, _, snapshotTurn2) = await newResolver.ResolveTurnVisualStateAsync(
            character: character,
            session: session,
            userMessage: "Turn 2 message",
            assistantReply: "Turn 2 reply",
            currentMood: CharacterMood.Neutral,
            turnId: Guid.NewGuid()
        );

        // 4. INVARIANT: Turn 1 snapshot remains STRICTLY FROZEN at 0.45 / 0.70 (Zero State Drift)
        Assert.Contains("\"weight\":0.45", snapshotTurn1.GenerationProfile.ParametersJson);
        Assert.Contains("\"endAt\":0.70", snapshotTurn1.GenerationProfile.ParametersJson);

        // Turn 2 snapshot has new 0.20 / 0.30 configuration
        Assert.Contains("\"weight\":0.20", snapshotTurn2.GenerationProfile.ParametersJson);
        Assert.Contains("\"endAt\":0.30", snapshotTurn2.GenerationProfile.ParametersJson);
    }

    [Fact]
    public async Task VisualGenerationProfileProvider_ResolvesConfigurableIPAdapterSettings_FreezesIntoVisualSnapshot()
    {
        // 1. Setup custom configuration for IP-Adapter weights
        var inMemoryConfig = new Dictionary<string, string?>
        {
            ["AiProviders:ImageGeneration:DefaultWorkflow"] = "VisualIdentityV2",
            ["AiProviders:ImageGeneration:DefaultWorkflowVersion"] = "2",
            ["AiProviders:ImageGeneration:IPAdapter:Weight"] = "0.35",
            ["AiProviders:ImageGeneration:IPAdapter:EndAt"] = "0.60"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemoryConfig).Build();

        var profileProvider = new VisualGenerationProfileProvider(configuration);
        var resolver = new VisualStateResolver(
            unitOfWork: null!,
            sceneStateTracker: null,
            profileProvider: profileProvider,
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<VisualStateResolver>.Instance
        );

        var visualIdentity = new CharacterVisualIdentity(
            Face: "delicate youthful features",
            Hair: "long flowing silver-white hair",
            Eyes: "blue eyes",
            Skin: "fair skin",
            Body: "slender",
            AgeAppearance: "early 20s",
            ClothingStyle: "starry robe",
            Accessories: null,
            VisualTraits: null,
            CanonicalReferenceUrl: "canonical.png"
        );

        var character = new Character("Aria", "Mage", "https://example.com/avatar.jpg", "Friendly", "Hello", "Fantasy", visualIdentity: visualIdentity);

        var session = new ChatSession(character.Id, Guid.NewGuid(), "Chat with Aria");

        // 2. Resolve turn visual state
        var (_, _, snapshot) = await resolver.ResolveTurnVisualStateAsync(
            character: character,
            session: session,
            userMessage: "Hello Aria",
            assistantReply: "Greetings traveler",
            currentMood: CharacterMood.Happy,
            turnId: Guid.NewGuid()
        );

        // 3. Assert GenerationProfile is dynamically configured and frozen into snapshot
        Assert.Equal("VisualIdentityV2", snapshot.GenerationProfile.Workflow);
        Assert.Equal(2, snapshot.GenerationProfile.WorkflowVersion);
        Assert.Contains("\"weight\":0.35", snapshot.GenerationProfile.ParametersJson);
        Assert.Contains("\"endAt\":0.60", snapshot.GenerationProfile.ParametersJson);
    }

    private sealed class ComfyUIImageGenerationIntegrationTests_InMemoryStorageService : IStorageService
    {
        public Task<string> SaveImageAsync(byte[] imageBytes, string fileName, string contentType = "image/jpeg", CancellationToken ct = default)
            => Task.FromResult($"/uploads/{fileName}");
        public Task<string> SaveBase64ImageAsync(string base64Data, string fileName, CancellationToken ct = default)
            => Task.FromResult($"/uploads/{fileName}");
        public Task<bool> DeleteFileAsync(string fileUrl, CancellationToken ct = default)
            => Task.FromResult(true);
    }
}
