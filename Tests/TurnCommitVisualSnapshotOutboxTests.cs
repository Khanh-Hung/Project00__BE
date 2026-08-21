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

        // Turn 1: Initial state -> Living Room, Sofa, White Dress, Revision = 1 (0 + 1)
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

        // Turn 2: Move to Window -> Position = Beside Window, Location and Outfit stay identical, Revision = 2
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

        // Turn 3: Change outfit -> Outfit = Black Dress, Position stays Beside Window, Location stays Living Room, Revision = 3
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

        // Turn 1 Invariant: Revision = 1, Living Room, Sofa, White Dress
        Assert.Equal(1, payload1.Snapshot.SceneRevision);
        Assert.Equal("Living Room", payload1.Snapshot.SceneState.CurrentLocation);
        Assert.Equal("Sofa", payload1.Snapshot.SceneState.CurrentPosition);
        Assert.Equal("White Dress", payload1.Snapshot.SceneState.CurrentOutfit);
        Assert.Equal("https://cloud.storage/elysia_canonical.png", payload1.Snapshot.IdentityReferenceUrl);
        Assert.Null(payload1.Snapshot.PreviousSceneImageUrl);

        // Turn 2 Invariant: Revision = 2, Living Room, Beside Window, White Dress
        Assert.Equal(2, payload2.Snapshot.SceneRevision);
        Assert.Equal("Living Room", payload2.Snapshot.SceneState.CurrentLocation);
        Assert.Equal("Beside Window", payload2.Snapshot.SceneState.CurrentPosition);
        Assert.Equal("White Dress", payload2.Snapshot.SceneState.CurrentOutfit);
        Assert.Equal("Walking toward window", payload2.Snapshot.TransientState?.Action);
        Assert.Equal("Gentle smile", payload2.Snapshot.TransientState?.Expression);

        // Turn 3 Invariant: Revision = 3, Living Room, Beside Window, Black Dress
        Assert.Equal(3, payload3.Snapshot.SceneRevision);
        Assert.Equal("Living Room", payload3.Snapshot.SceneState.CurrentLocation);
        Assert.Equal("Beside Window", payload3.Snapshot.SceneState.CurrentPosition);
        Assert.Equal("Black Dress", payload3.Snapshot.SceneState.CurrentOutfit);
    }

    [Fact]
    public async Task Worker_Compiles_Prompt_Deterministically_From_Snapshot_Without_Reading_Current_Session()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<ProjectDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IVoicePromptCompiler, VoicePromptCompiler>();
        services.AddSingleton<IVisualPromptCompiler, VisualPromptCompiler>();
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
            VisualIdentity: new CharacterVisualIdentity(
                Hair: "platinum blonde hair",
                ClothingStyle: "White Dress",
                CanonicalReferenceUrl: "https://cloud.storage/elysia_canonical.png"
            ),
            SceneState: new SessionSceneState(
                CurrentLocation: "Living Room",
                CurrentPosition: "Sofa",
                CurrentOutfit: "White Silk Dress",
                SceneRevision: 1
            ),
            TransientState: new TransientVisualState(Pose: "Sitting gracefully", Expression: "Gentle smile"),
            IdentityReferenceUrl: "https://cloud.storage/elysia_canonical.png"
        );

        using (var seedScope = scopeFactory.CreateScope())
        {
            var ctx = seedScope.ServiceProvider.GetRequiredService<ProjectDbContext>();

            // The session in database has EVOLVED far ahead to Turn 10: Crimson Castle, Red Empress Gown
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
                Snapshot: snapshotTurn1
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

        // 3. STRICT ASSERTION: Worker generated image using compiled prompt directly from Turn 1 snapshot, NOT the evolved database state!
        var generatedReq = capturedImageRequests[0];
        Assert.Contains("White Silk Dress", generatedReq.Prompt);
        Assert.Contains("Sofa", generatedReq.Prompt);
        Assert.Contains("Living Room", generatedReq.Prompt);
        Assert.Equal("https://cloud.storage/elysia_canonical.png", generatedReq.ReferenceImageUrl);
        Assert.DoesNotContain("Crimson Castle", generatedReq.Prompt);
        Assert.DoesNotContain("Red Empress Gown", generatedReq.Prompt);
    }

    [Fact]
    public async Task PreviousSceneImageUrl_Resolves_From_Exact_Revision_N_Minus_1()
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
            visualIdentity: new CharacterVisualIdentity(ClothingStyle: "White Dress")
        ) { Id = charId };
        await context.Characters.AddAsync(character);

        // Session at Revision 1 having a committed scene image
        var initialScene = new SessionSceneState(
            CurrentLocation: "Living Room",
            CurrentPosition: "Sofa",
            CurrentOutfit: "White Dress",
            SceneRevision: 1,
            LastSceneImageUrl: "https://cloud.storage/scene_rev1.png"
        );

        var session = new ChatSession(charId, userId, "Continuity Session", sceneState: initialScene);
        await context.ChatSessions.AddAsync(session);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var fakeUserProvider = new FakeUserProvider(userId.ToString());
        var fakeMemoryService = new FakeMemoryService();
        var contextEngine = new RoleplayContextEngine(unitOfWork, fakeMemoryService, fakeUserProvider, NullLogger<RoleplayContextEngine>.Instance);
        var mockTrigger = new MockMemoryExtractionTrigger();
        var sequentialTracker = new SequentialSceneStateTracker();

        var runtime = new CharacterRuntime(
            unitOfWork,
            contextEngine,
            new ConfigurableLLMService("Ta đang đi tới cửa sổ."),
            mockTrigger,
            new VoicePromptCompiler(),
            new MockVoiceService(),
            new VisualPromptCompiler(),
            new MockImageService(),
            NullLogger<CharacterRuntime>.Instance,
            sequentialTracker
        );

        // Turn 2 executes: Target Revision is 2 (1 + 1). Previous scene image from Revision 1 MUST be resolved!
        sequentialTracker.NextDelta = new SceneStateDelta(PositionChange: "Beside Window");
        await runtime.ProcessTurnAsync(new CharacterTurnRequest(
            UserId: userId,
            CharacterId: charId,
            SessionId: session.Id,
            UserMessage: "Em đứng đó làm gì?",
            TurnId: Guid.NewGuid(),
            Options: new CharacterTurnOptions(GenerateImage: true)
        ));

        var outboxMsg = await context.OutboxMessages.FirstOrDefaultAsync(m => m.EventType == OutboxEventTypes.SceneImageGeneration);
        Assert.NotNull(outboxMsg);

        var payload = JsonSerializer.Deserialize<SceneImageGenerationOutboxPayload>(outboxMsg.PayloadJson);
        Assert.NotNull(payload?.Snapshot);
        Assert.Equal(2, payload.Snapshot.SceneRevision);
        Assert.Equal("https://cloud.storage/scene_rev1.png", payload.Snapshot.PreviousSceneImageUrl);
    }

    [Fact]
    public async Task Streaming_And_NonStreaming_Produce_Identical_VisualSnapshot_Semantics()
    {
        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var character = new Character(
            name: "Elysia",
            title: "Herrscher of Human: Ego",
            avatarUrl: "https://cloud.storage/elysia_avatar.png",
            personalityPrompt: "Gentle and loving",
            greeting: "Hi there!",
            category: "Anime",
            visualIdentity: new CharacterVisualIdentity(
                Gender: "Female",
                Hair: "Pink",
                ClothingStyle: "Holy Silk Dress",
                CanonicalReferenceUrl: "https://cloud.storage/canonical.png"
            )
        ) { Id = charId };

        // Test Non-streaming turn
        var db1 = Guid.NewGuid().ToString();
        await using var ctx1 = new ProjectDbContext(new DbContextOptionsBuilder<ProjectDbContext>().UseInMemoryDatabase(db1).Options);
        await ctx1.Characters.AddAsync(character);
        var session1 = new ChatSession(charId, userId, "Session 1");
        await ctx1.ChatSessions.AddAsync(session1);
        await ctx1.SaveChangesAsync();

        var tracker1 = new SequentialSceneStateTracker { NextDelta = new SceneStateDelta(PositionChange: "Grand Altar", ExpressionChange: "Warm smile") };
        var runtime1 = new CharacterRuntime(
            new UnitOfWork(ctx1),
            new RoleplayContextEngine(new UnitOfWork(ctx1), new FakeMemoryService(), new FakeUserProvider(userId.ToString()), NullLogger<RoleplayContextEngine>.Instance),
            new ConfigurableLLMService("```json\n{\"reply\": \"Chào anh!\", \"mood\": \"Happy\", \"moodIntensity\": 80, \"affectionDelta\": 2}\n```"),
            new MockMemoryExtractionTrigger(),
            new VoicePromptCompiler(),
            new MockVoiceService(),
            new VisualPromptCompiler(),
            new MockImageService(),
            NullLogger<CharacterRuntime>.Instance,
            tracker1
        );

        await runtime1.ProcessTurnAsync(new CharacterTurnRequest(userId, charId, session1.Id, "Hello", Guid.NewGuid(), new CharacterTurnOptions(GenerateImage: true)));
        var msg1 = await ctx1.OutboxMessages.FirstAsync(m => m.EventType == OutboxEventTypes.SceneImageGeneration);
        var snapshot1 = JsonSerializer.Deserialize<SceneImageGenerationOutboxPayload>(msg1.PayloadJson)!.Snapshot;

        // Test Streaming turn
        var db2 = Guid.NewGuid().ToString();
        await using var ctx2 = new ProjectDbContext(new DbContextOptionsBuilder<ProjectDbContext>().UseInMemoryDatabase(db2).Options);
        await ctx2.Characters.AddAsync(character);
        var session2 = new ChatSession(charId, userId, "Session 2");
        await ctx2.ChatSessions.AddAsync(session2);
        await ctx2.SaveChangesAsync();

        var tracker2 = new SequentialSceneStateTracker { NextDelta = new SceneStateDelta(PositionChange: "Grand Altar", ExpressionChange: "Warm smile") };
        var runtime2 = new CharacterRuntime(
            new UnitOfWork(ctx2),
            new RoleplayContextEngine(new UnitOfWork(ctx2), new FakeMemoryService(), new FakeUserProvider(userId.ToString()), NullLogger<RoleplayContextEngine>.Instance),
            new ConfigurableLLMService("```json\n{\"reply\": \"Chào anh!\", \"mood\": \"Happy\", \"moodIntensity\": 80, \"affectionDelta\": 2}\n```"),
            new MockMemoryExtractionTrigger(),
            new VoicePromptCompiler(),
            new MockVoiceService(),
            new VisualPromptCompiler(),
            new MockImageService(),
            NullLogger<CharacterRuntime>.Instance,
            tracker2
        );

        await foreach (var _ in runtime2.ProcessTurnStreamAsync(new CharacterTurnRequest(userId, charId, session2.Id, "Hello", Guid.NewGuid(), new CharacterTurnOptions(GenerateImage: true))))
        {
        }
        var msg2 = await ctx2.OutboxMessages.FirstAsync(m => m.EventType == OutboxEventTypes.SceneImageGeneration);
        var snapshot2 = JsonSerializer.Deserialize<SceneImageGenerationOutboxPayload>(msg2.PayloadJson)!.Snapshot;

        // Assert 100% semantic identity between streaming and non-streaming
        Assert.Equal(snapshot1.SceneRevision, snapshot2.SceneRevision);
        Assert.Equal(snapshot1.SceneState.CurrentLocation, snapshot2.SceneState.CurrentLocation);
        Assert.Equal(snapshot1.SceneState.CurrentPosition, snapshot2.SceneState.CurrentPosition);
        Assert.Equal(snapshot1.SceneState.CurrentOutfit, snapshot2.SceneState.CurrentOutfit);
        Assert.Equal(snapshot1.TransientState?.Expression, snapshot2.TransientState?.Expression);
        Assert.Equal(snapshot1.IdentityReferenceUrl, snapshot2.IdentityReferenceUrl);
    }

    [Fact]
    public async Task End_To_End_MultiTurn_Image_Lifecycle_Continuity_Chain()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<ProjectDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IVoicePromptCompiler, VoicePromptCompiler>();
        services.AddSingleton<IVisualPromptCompiler, VisualPromptCompiler>();
        services.AddSingleton<IVoiceGenerationService, MockVoiceService>();

        int imageCounter = 0;
        var capturedRequests = new List<ImageGenerationRequest>();
        var dynamicImageService = new SequentialImageService(capturedRequests, () => $"https://images.storage/scene_frame_{++imageCounter}.png");
        services.AddSingleton<IImageGenerationService>(dynamicImageService);
        services.AddSingleton<IMemoryExtractionTrigger, MockMemoryExtractionTrigger>();
        services.AddLogging();

        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        using (var initScope = scopeFactory.CreateScope())
        {
            var ctx = initScope.ServiceProvider.GetRequiredService<ProjectDbContext>();
            var character = new Character(
                name: "Elysia",
                title: "Herrscher of Human",
                avatarUrl: "https://cloud.storage/elysia_avatar.png",
                personalityPrompt: "Gentle and loving",
                greeting: "Hi there!",
                category: "Anime",
                visualIdentity: new CharacterVisualIdentity(
                    ClothingStyle: "White Dress",
                    CanonicalReferenceUrl: "https://cloud.storage/elysia_canonical.png"
                )
            ) { Id = charId };
            await ctx.Characters.AddAsync(character);

            var session = new ChatSession(charId, userId, "End-to-End Continuity Session") { Id = sessionId };
            await ctx.ChatSessions.AddAsync(session);
            await ctx.SaveChangesAsync();
        }

        var tracker = new SequentialSceneStateTracker();

        // --- TURN 1 ---
        using (var turn1Scope = scopeFactory.CreateScope())
        {
            var ctx = turn1Scope.ServiceProvider.GetRequiredService<ProjectDbContext>();
            var uow = new UnitOfWork(ctx);
            var runtime = new CharacterRuntime(
                uow,
                new RoleplayContextEngine(uow, new FakeMemoryService(), new FakeUserProvider(userId.ToString()), NullLogger<RoleplayContextEngine>.Instance),
                new ConfigurableLLMService("Chào bạn!"),
                new MockMemoryExtractionTrigger(),
                new VoicePromptCompiler(),
                new MockVoiceService(),
                new VisualPromptCompiler(),
                dynamicImageService,
                NullLogger<CharacterRuntime>.Instance,
                tracker
            );

            tracker.NextDelta = new SceneStateDelta(LocationChange: "Living Room", PositionChange: "Sofa", OutfitChange: "White Dress");
            await runtime.ProcessTurnAsync(new CharacterTurnRequest(userId, charId, sessionId, "Chào em!", Guid.NewGuid(), new CharacterTurnOptions(GenerateImage: true)));
        }

        // Process Outbox for Turn 1 -> Generates Image 1 and updates SessionSceneState.LastSceneImageUrl
        var processor = new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance);
        var processed1 = await processor.ProcessPendingOutboxMessagesAsync();
        Assert.True(processed1 >= 1);
        Assert.Single(capturedRequests);
        Assert.Null(capturedRequests[0].PreviousSceneImageUrl); // Turn 1 has no previous frame

        // --- TURN 2 ---
        using (var turn2Scope = scopeFactory.CreateScope())
        {
            var ctx = turn2Scope.ServiceProvider.GetRequiredService<ProjectDbContext>();
            var uow = new UnitOfWork(ctx);
            var runtime = new CharacterRuntime(
                uow,
                new RoleplayContextEngine(uow, new FakeMemoryService(), new FakeUserProvider(userId.ToString()), NullLogger<RoleplayContextEngine>.Instance),
                new ConfigurableLLMService("Ta bước lại gần cửa sổ."),
                new MockMemoryExtractionTrigger(),
                new VoicePromptCompiler(),
                new MockVoiceService(),
                new VisualPromptCompiler(),
                dynamicImageService,
                NullLogger<CharacterRuntime>.Instance,
                tracker
            );

            tracker.NextDelta = new SceneStateDelta(PositionChange: "Beside Window", ActionChange: "Walking toward window");
            await runtime.ProcessTurnAsync(new CharacterTurnRequest(userId, charId, sessionId, "Em đi đâu thế?", Guid.NewGuid(), new CharacterTurnOptions(GenerateImage: true)));
        }

        // Process Outbox for Turn 2
        var processed2 = await processor.ProcessPendingOutboxMessagesAsync();
        Assert.True(processed2 >= 1);
        Assert.Equal(2, capturedRequests.Count);

        // Assert: Turn 2 request MUST contain PreviousSceneImageUrl pointing to Image 1!
        Assert.Equal("https://images.storage/scene_frame_1.png", capturedRequests[1].PreviousSceneImageUrl);
        Assert.Equal("https://cloud.storage/elysia_canonical.png", capturedRequests[1].ReferenceImageUrl);

        // --- TURN 3 ---
        using (var turn3Scope = scopeFactory.CreateScope())
        {
            var ctx = turn3Scope.ServiceProvider.GetRequiredService<ProjectDbContext>();
            var uow = new UnitOfWork(ctx);
            var runtime = new CharacterRuntime(
                uow,
                new RoleplayContextEngine(uow, new FakeMemoryService(), new FakeUserProvider(userId.ToString()), NullLogger<RoleplayContextEngine>.Instance),
                new ConfigurableLLMService("Ngắm trăng cùng anh nhé."),
                new MockMemoryExtractionTrigger(),
                new VoicePromptCompiler(),
                new MockVoiceService(),
                new VisualPromptCompiler(),
                dynamicImageService,
                NullLogger<CharacterRuntime>.Instance,
                tracker
            );

            tracker.NextDelta = new SceneStateDelta(OutfitChange: "Black Dress", PoseChange: "Looking outside");
            await runtime.ProcessTurnAsync(new CharacterTurnRequest(userId, charId, sessionId, "Trăng đẹp thật.", Guid.NewGuid(), new CharacterTurnOptions(GenerateImage: true)));
        }

        // Process Outbox for Turn 3
        var processed3 = await processor.ProcessPendingOutboxMessagesAsync();
        Assert.True(processed3 >= 1);
        Assert.Equal(3, capturedRequests.Count);

        // Assert: Turn 3 request MUST contain PreviousSceneImageUrl pointing to Image 2!
        Assert.Equal("https://images.storage/scene_frame_2.png", capturedRequests[2].PreviousSceneImageUrl);
        Assert.Contains("Black Dress", capturedRequests[2].Prompt);
        Assert.Contains("Beside Window", capturedRequests[2].Prompt);
        Assert.Contains("Living Room", capturedRequests[2].Prompt);
    }

    private sealed class SequentialImageService : IImageGenerationService
    {
        private readonly List<ImageGenerationRequest> _capturedRequests;
        private readonly Func<string> _urlGenerator;

        public SequentialImageService(List<ImageGenerationRequest> capturedRequests, Func<string> urlGenerator)
        {
            _capturedRequests = capturedRequests;
            _urlGenerator = urlGenerator;
        }

        public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default)
        {
            var url = _urlGenerator();
            _capturedRequests.Add(new ImageGenerationRequest(prompt, width, height));
            return Task.FromResult(url);
        }

        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default)
        {
            var url = _urlGenerator();
            _capturedRequests.Add(request);
            return Task.FromResult(url);
        }
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
                SceneRevision: 0
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

        public async IAsyncEnumerable<string> GenerateRoleplayTurnStreamAsync(RoleplayContext context, CancellationToken ct = default)
        {
            yield return _reply;
            await Task.CompletedTask;
        }

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
