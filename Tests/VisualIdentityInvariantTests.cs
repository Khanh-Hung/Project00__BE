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

    private static (ProjectDbContext db, ImageGenerationJobHandler handler, CountingImageService imageService) CreateHarness(string dbName)
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        var db = new ProjectDbContext(options);
        var imageService = new CountingImageService();
        var visualCompiler = new VisualPromptCompiler();
        var handler = new ImageGenerationJobHandler(db, visualCompiler, imageService, NullLogger<ImageGenerationJobHandler>.Instance);
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
        var handlerB = new ImageGenerationJobHandler(db, visualCompiler, imageService, NullLogger<ImageGenerationJobHandler>.Instance);

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

    [Fact]
    public async Task ConcurrencyTestB_ExpiredLeaseRace_StaleWorkerCannotOverwriteNewOwner()
    {
        var dbName = Guid.NewGuid().ToString();
        var (db, handler, _) = CreateHarness(dbName);

        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        // Worker A had claimed the job but lease expired
        var job = new ImageGenerationJob(sessionId, turnId, Guid.NewGuid(), 1, requestId);
        job.TryClaim("worker-A", TimeSpan.FromSeconds(1), Clock.Now.AddMinutes(-5));
        await db.ImageGenerationJobs.AddAsync(job);
        await db.SaveChangesAsync();

        // Worker B claims the expired job
        job.TryClaim("worker-B", TimeSpan.FromMinutes(2), Clock.Now);
        await db.SaveChangesAsync();

        var snapshot = CreateTestSnapshot(sessionId, turnId, revision: 1);
        var payload = new SceneImageGenerationOutboxPayload(turnId, snapshot.CharacterId, Guid.NewGuid(), snapshot, requestId);

        // If Worker A attempts to run with stale identity "worker-A" while Worker B owns it
        var resA = await handler.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-A", Clock.Now);
        Assert.Equal(JobExecutionStatus.Deferred, resA.Status);
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
            ProviderJobId: "prompt-recovered"
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
            ParametersJson: "{ malformed json..."
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
            ReferenceImageUrl: null
        );

        // Must throw GpuNonTransientException when resolvedReferenceImageName is empty
        await Assert.ThrowsAsync<GpuNonTransientException>(() =>
        {
            builder.BuildWorkflow(request, string.Empty);
            return Task.CompletedTask;
        });
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
