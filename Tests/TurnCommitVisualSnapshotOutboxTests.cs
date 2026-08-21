using Application.Abstractions.Auth;
using Application.Common;
using Application.DTOs;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace Project.Tests;

public class TurnCommitVisualSnapshotOutboxTests
{
    [Fact]
    public async Task MultiTurn_Snapshots_In_Outbox_Maintain_Exact_Spatial_And_Outfit_Isolation()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var character = new Character(
            name: "Elysia",
            title: "Herrscher of Human: Ego",
            avatarUrl: "https://cloud.storage/elysia_avatar.png",
            personalityPrompt: "Gentle and loving",
            greeting: "Hi there!",
            category: "Anime",
            visualIdentity: new CharacterVisualIdentity(
                Gender: "Female",
                Face: "Delicate porcelain face, gentle smile",
                Hair: "Long platinum blonde hair",
                Eyes: "Emerald green eyes",
                Skin: "Fair skin",
                Body: "Slender athletic build",
                ClothingStyle: "White Dress",
                CanonicalReferenceUrl: "https://cloud.storage/elysia_canonical.png"
            )
        ) { Id = charId };
        await context.Characters.AddAsync(character);

        var session = new ChatSession(charId, userId, "Visual Continuity Session");
        await context.ChatSessions.AddAsync(session);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var fakeUserProvider = new FakeUserProvider(userId.ToString());
        var fakeMemoryService = new FakeMemoryService();
        var contextEngine = new RoleplayContextEngine(unitOfWork, fakeMemoryService, fakeUserProvider, NullLogger<RoleplayContextEngine>.Instance);
        var mockTrigger = new MockMemoryExtractionTrigger();

        // Sequential state tracker simulating multi-turn physical progression
        var sequentialTracker = new SequentialSceneStateTracker();

        var runtime = new CharacterRuntime(
            unitOfWork,
            contextEngine,
            new ConfigurableLLMService("Ta đang ở phòng khách đây!"),
            mockTrigger,
            new VoicePromptCompiler(),
            new MockVoiceService(),
            new VisualPromptCompiler(),
            new MockImageService(),
            NullLogger<CharacterRuntime>.Instance,
            sequentialTracker
        );

        // Turn 1: Initial state -> Living Room, Sofa, White Dress, Revision 2 (1 + 1)
        sequentialTracker.NextDelta = new SceneStateDelta(
            LocationChange: "Living Room",
            PositionChange: "Sofa",
            OutfitChange: "White Dress"
        );
        await runtime.ProcessTurnAsync(new CharacterTurnRequest(
            UserId: userId,
            CharacterId: charId,
            SessionId: session.Id,
            UserMessage: "Chào Elysia!",
            TurnId: Guid.NewGuid(),
            Options: new CharacterTurnOptions(GenerateImage: true)
        ));

        // Turn 2: Move to Window -> Position = Beside Window, Location and Outfit stay identical
        sequentialTracker.NextDelta = new SceneStateDelta(
            PositionChange: "Beside Window",
            ActionChange: "Walking toward window",
            ExpressionChange: "Gentle smile"
        );
        await runtime.ProcessTurnAsync(new CharacterTurnRequest(
            UserId: userId,
            CharacterId: charId,
            SessionId: session.Id,
            UserMessage: "Em làm gì thế?",
            TurnId: Guid.NewGuid(),
            Options: new CharacterTurnOptions(GenerateImage: true)
        ));

        // Turn 3: Change outfit -> Outfit = Black Dress, Position stays Beside Window, Location stays Living Room
        sequentialTracker.NextDelta = new SceneStateDelta(
            OutfitChange: "Black Dress"
        );
        await runtime.ProcessTurnAsync(new CharacterTurnRequest(
            UserId: userId,
            CharacterId: charId,
            SessionId: session.Id,
            UserMessage: "Em thay đồ dạ hội à?",
            TurnId: Guid.NewGuid(),
            Options: new CharacterTurnOptions(GenerateImage: true)
        ));

        // Verify that 3 SceneImageGeneration Outbox messages were atomically written
        var outboxMessages = await context.OutboxMessages
            .Where(m => m.EventType == OutboxEventTypes.SceneImageGeneration)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();

        Assert.Equal(3, outboxMessages.Count);

        var payload1 = JsonSerializer.Deserialize<SceneImageGenerationOutboxPayload>(outboxMessages[0].PayloadJson);
        var payload2 = JsonSerializer.Deserialize<SceneImageGenerationOutboxPayload>(outboxMessages[1].PayloadJson);
        var payload3 = JsonSerializer.Deserialize<SceneImageGenerationOutboxPayload>(outboxMessages[2].PayloadJson);

        Assert.NotNull(payload1?.Snapshot);
        Assert.NotNull(payload2?.Snapshot);
        Assert.NotNull(payload3?.Snapshot);

        // Assert Invariant: Snapshot 1 MUST retain Living Room, Sofa, White Dress
        Assert.Equal("Living Room", payload1.Snapshot.SceneState.CurrentLocation);
        Assert.Equal("Sofa", payload1.Snapshot.SceneState.CurrentPosition);
        Assert.Equal("White Dress", payload1.Snapshot.SceneState.CurrentOutfit);
        Assert.Equal("https://cloud.storage/elysia_canonical.png", payload1.Snapshot.IdentityReferenceUrl);

        // Assert Invariant: Snapshot 2 MUST retain Living Room, Beside Window, White Dress
        Assert.Equal("Living Room", payload2.Snapshot.SceneState.CurrentLocation);
        Assert.Equal("Beside Window", payload2.Snapshot.SceneState.CurrentPosition);
        Assert.Equal("White Dress", payload2.Snapshot.SceneState.CurrentOutfit);
        Assert.Equal("Walking toward window", payload2.Snapshot.TransientState?.Action);
        Assert.Equal("Gentle smile", payload2.Snapshot.TransientState?.Expression);

        // Assert Invariant: Snapshot 3 MUST retain Living Room, Beside Window, Black Dress
        Assert.Equal("Living Room", payload3.Snapshot.SceneState.CurrentLocation);
        Assert.Equal("Beside Window", payload3.Snapshot.SceneState.CurrentPosition);
        Assert.Equal("Black Dress", payload3.Snapshot.SceneState.CurrentOutfit);
    }

    [Fact]
    public async Task Worker_Processes_From_Snapshot_Without_Reading_Current_Session()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<ProjectDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IVoicePromptCompiler, VoicePromptCompiler>();
        services.AddSingleton<IVoiceGenerationService, MockVoiceService>();

        var capturedImageRequests = new List<ImageGenerationRequest>();
        var recordingImageService = new RecordingImageService(capturedImageRequests);
        services.AddSingleton<IImageGenerationService>(recordingImageService);
        services.AddSingleton<IMemoryExtractionTrigger, MockMemoryExtractionTrigger>();
        services.AddLogging();

        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        // 1. Seed Turn 1 Snapshot into Outbox: Living Room, White Dress, Sofa
        var snapshotTurn1 = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: sessionId,
            CharacterId: charId,
            SceneRevision: 1,
            VisualIdentity: new CharacterVisualIdentity(CanonicalReferenceUrl: "https://cloud.storage/elysia.png"),
            SceneState: new SessionSceneState(
                CurrentLocation: "Living Room",
                CurrentPosition: "Sofa",
                CurrentOutfit: "White Dress",
                SceneRevision: 1
            ),
            TransientState: new TransientVisualState(Pose: "Sitting", Expression: "Smiling"),
            IdentityReferenceUrl: "https://cloud.storage/elysia.png"
        );

        using (var seedScope = scopeFactory.CreateScope())
        {
            var ctx = seedScope.ServiceProvider.GetRequiredService<ProjectDbContext>();

            // The session in database has EVOLVED far ahead to Turn 10: Crimson Castle, Red Dress, Standing
            var evolvedSession = new ChatSession(charId, Guid.NewGuid(), "Evolved Session") { Id = sessionId };
            evolvedSession.UpdateSceneState(new SessionSceneState(
                CurrentLocation: "Crimson Castle",
                CurrentPosition: "Throne",
                CurrentOutfit: "Red Empress Gown",
                SceneRevision: 10
            ));
            await ctx.ChatSessions.AddAsync(evolvedSession);

            var scenePayload = new SceneImageGenerationOutboxPayload(
                TurnId: snapshotTurn1.TurnId,
                CharacterId: charId,
                UserId: Guid.NewGuid(),
                Snapshot: snapshotTurn1,
                Prompt: "1girl, elysia, white dress, sitting on sofa, gentle smile"
            );
            await ctx.OutboxMessages.AddAsync(new OutboxMessage(
                eventType: OutboxEventTypes.SceneImageGeneration,
                payloadJson: JsonSerializer.Serialize(scenePayload)
            ));
            await ctx.SaveChangesAsync();
        }

        // 2. Outbox Processor runs
        var processor = new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance);
        var processed = await processor.ProcessPendingOutboxMessagesAsync();

        Assert.Equal(1, processed);
        Assert.Single(capturedImageRequests);

        // 3. STRICT ASSERTION: Worker generated image using Turn 1 snapshot prompt & reference, NOT the evolved database state!
        var generatedReq = capturedImageRequests[0];
        Assert.Contains("white dress", generatedReq.Prompt);
        Assert.Contains("sofa", generatedReq.Prompt);
        Assert.DoesNotContain("Crimson Castle", generatedReq.Prompt);
        Assert.DoesNotContain("Red Empress Gown", generatedReq.Prompt);
    }

    private sealed class SequentialSceneStateTracker : ISceneStateTrackerService
    {
        public SceneStateDelta? NextDelta { get; set; }

        public Task<SessionSceneState> TrackAndExtractStateAsync(Character character, SessionSceneState? currentState, string userMessage, string assistantMessage, CancellationToken ct = default)
        {
            var baseState = currentState ?? new SessionSceneState(
                CurrentLocation: character.Title ?? "Sanctuary",
                CurrentPosition: "Central Area",
                CurrentOutfit: character.VisualIdentity?.ClothingStyle ?? "Canonical Dress",
                SceneRevision: 1
            );
            return Task.FromResult(baseState.ApplyDelta(NextDelta ?? new SceneStateDelta()));
        }

        public Task<SceneStateDelta> TrackAndExtractDeltaAsync(Character character, SessionSceneState? currentState, string userMessage, string assistantMessage, CancellationToken ct = default)
        {
            return Task.FromResult(NextDelta ?? new SceneStateDelta());
        }
    }

    private sealed class RecordingImageService : IImageGenerationService
    {
        private readonly List<ImageGenerationRequest> _capturedRequests;

        public RecordingImageService(List<ImageGenerationRequest> capturedRequests)
        {
            _capturedRequests = capturedRequests;
        }

        public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default)
        {
            _capturedRequests.Add(new ImageGenerationRequest(prompt, width, height));
            return Task.FromResult("https://generated.images/scene.png");
        }

        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default)
        {
            _capturedRequests.Add(request);
            return Task.FromResult("https://generated.images/scene.png");
        }
    }

    private sealed class FakeUserProvider : ICurrentUserProvider
    {
        public FakeUserProvider(string? currentUserId) => CurrentUserId = currentUserId;
        public string? CurrentUserId { get; }
        public string? CurrentUserName => "TestUser";
        public string? CurrentUserEmail => "test@example.com";
        public string? CurrentUserRole => "User";
    }

    private sealed class FakeMemoryService : IMemoryService
    {
        public Task<IReadOnlyList<CharacterMemory>> GetRelevantMemoriesAsync(Guid userId, Guid characterId, int maxCount = 6, string? queryText = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CharacterMemory>>(new List<CharacterMemory>());

        public Task<MemoryExtractionMetrics> StoreCandidatesAsync(Guid userId, Guid characterId, Guid? sessionId, IEnumerable<MemoryCandidate> candidates, CancellationToken ct = default) =>
            Task.FromResult(new MemoryExtractionMetrics(0, 0, 0, 0, 0));
    }

    private sealed class ConfigurableLLMService : ILLMService
    {
        private readonly string _reply;

        public ConfigurableLLMService(string reply) => _reply = reply;

        public Task<RoleplayTurnResult> GenerateRoleplayTurnAsync(RoleplayContext context, CancellationToken ct = default) =>
            Task.FromResult(new RoleplayTurnResult(_reply, CharacterMood.Happy, 80, 2, null));

        public IAsyncEnumerable<string> GenerateRoleplayTurnStreamAsync(RoleplayContext context, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RoleplayTurnResult> GenerateRoleplayTurnAsync(Character character, IReadOnlyCollection<ChatMessage> history, string newUserMessage, CharacterRelationship? relationship = null, IReadOnlyCollection<CharacterMemory>? memories = null, CancellationToken ct = default) =>
            Task.FromResult(new RoleplayTurnResult(_reply, CharacterMood.Happy, 80, 2, null));
        public Task<string> GenerateRoleplayResponseAsync(Character character, IReadOnlyCollection<ChatMessage> history, string newUserMessage, CharacterRelationship? relationship = null, CancellationToken ct = default) => Task.FromResult(_reply);
        public Task<GeneratedCharacterDto> GenerateCharacterProfileAsync(string idea, string? category = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GenerateRandomIdeasAsync(int count = 4, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GenerateRoleplaySuggestionsAsync(Character character, IReadOnlyCollection<ChatMessage> history, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GenerateAvatarResponse> GenerateAvatarAsync(GenerateAvatarRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GenerateAvatarResponse> GenerateSceneImageAsync(GenerateSceneImageRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<MemoryCandidate>> ExtractMemoryCandidatesAsync(Character character, IReadOnlyCollection<ChatMessageDto> recentMessages, CancellationToken ct = default) => Task.FromResult(new List<MemoryCandidate>());
        public Task<ProactiveAiReachoutResult> GenerateProactiveReachoutAsync(Character character, UserProfile userProfile, CancellationToken ct = default) => Task.FromResult(new ProactiveAiReachoutResult("Hi", "Matched"));
    }

    private sealed class MockMemoryExtractionTrigger : IMemoryExtractionTrigger
    {
        public bool NotifyMessageSent(MemoryExtractionJob job) => true;
    }

    private sealed class MockVoiceService : IVoiceGenerationService
    {
        public Task<VoiceGenerationResult> GenerateVoiceAsync(VoiceGenerationRequest request, CancellationToken ct = default) =>
            Task.FromResult(new VoiceGenerationResult("https://audio.storage/voice.mp3", "audio/mpeg", 2));
    }

    private sealed class MockImageService : IImageGenerationService
    {
        public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default) =>
            Task.FromResult("https://image.storage/mock.png");

        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default) =>
            Task.FromResult("https://image.storage/mock.png");
    }
}
