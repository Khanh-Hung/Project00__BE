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

/// <summary>
/// Exhaustive 8-Turn End-to-End Visual Continuity Benchmark Test Suite.
/// Proves that Identity, Scene N-1 Artifacts, Prompt Events, and Physical Invariants
/// are maintained with mathematical precision across multi-turn roleplay progression.
/// </summary>
public class VisualContinuity8TurnBenchmarkTests
{
    [Fact]
    public async Task Benchmark_8Turn_Visual_Continuity_Chain_Preserves_All_Physical_Invariants()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<ProjectDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IVoicePromptCompiler, VoicePromptCompiler>();
        services.AddSingleton<IVisualPromptCompiler, VisualPromptCompiler>();
        services.AddSingleton<IVoiceGenerationService, MockVoiceService>();

        int imageCounter = 0;
        var capturedRequests = new List<ImageGenerationRequest>();
        var dynamicImageService = new SequentialImageService(capturedRequests, () => $"https://images.storage/elysia_frame_{++imageCounter}.png");
        services.AddSingleton<IImageGenerationService>(dynamicImageService);
        services.AddSingleton<IMemoryExtractionTrigger, MockMemoryExtractionTrigger>();
        services.AddLogging();

        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        const string canonicalIdentityUrl = "https://cloud.storage/elysia_canonical_master.png";

        // Seed Character and ChatSession
        using (var initScope = scopeFactory.CreateScope())
        {
            var ctx = initScope.ServiceProvider.GetRequiredService<ProjectDbContext>();
            var character = new Character(
                name: "Elysia",
                title: "Herrscher of Human: Ego",
                avatarUrl: "https://cloud.storage/elysia_avatar.png",
                personalityPrompt: "Gentle and loving",
                greeting: "Hi there!",
                category: "Anime",
                visualIdentity: new CharacterVisualIdentity(
                    Gender: "Female",
                    Hair: "Long platinum blonde hair",
                    Eyes: "Emerald green eyes",
                    Face: "Delicate porcelain face",
                    Skin: "Fair skin",
                    Body: "Slender athletic build",
                    ClothingStyle: "White Dress",
                    CanonicalReferenceUrl: canonicalIdentityUrl
                )
            ) { Id = charId };
            await ctx.Characters.AddAsync(character);

            var session = new ChatSession(charId, userId, "8-Turn Benchmark Session") { Id = sessionId };
            await ctx.ChatSessions.AddAsync(session);
            await ctx.SaveChangesAsync();
        }

        var tracker = new TestSceneTracker();
        var processor = new OutboxProcessorBackgroundService(scopeFactory, NullLogger<OutboxProcessorBackgroundService>.Instance);

        // Helper turn runner
        async Task RunTurnAsync(string userMsg, string aiReply, SceneStateDelta delta)
        {
            tracker.NextDelta = delta;
            using var turnScope = scopeFactory.CreateScope();
            var ctx = turnScope.ServiceProvider.GetRequiredService<ProjectDbContext>();
            var uow = new UnitOfWork(ctx);
            var runtime = new CharacterRuntime(
                uow,
                new RoleplayContextEngine(uow, new FakeMemoryService(), new FakeUserProvider(userId.ToString()), NullLogger<RoleplayContextEngine>.Instance),
                new ConfigurableLLMService(aiReply),
                new MockMemoryExtractionTrigger(),
                new VoicePromptCompiler(),
                new MockVoiceService(),
                new VisualPromptCompiler(),
                dynamicImageService,
                NullLogger<CharacterRuntime>.Instance,
                tracker
            );

            await runtime.ProcessTurnAsync(new CharacterTurnRequest(
                UserId: userId,
                CharacterId: charId,
                SessionId: sessionId,
                UserMessage: userMsg,
                TurnId: Guid.NewGuid(),
                Options: new CharacterTurnOptions(GenerateImage: true)
            ));

            await processor.ProcessPendingOutboxMessagesAsync();
        }

        // ==========================================
        // TURN 1: Elysia, White Dress, Sofa, Smile
        // ==========================================
        await RunTurnAsync(
            "Chào Elysia, em đang ở đâu thế?",
            "Ta đang ngồi ở sofa phòng khách nè!",
            new SceneStateDelta(LocationChange: "Living Room", PositionChange: "Sofa", OutfitChange: "White Dress", ExpressionChange: "Gentle smile")
        );

        // ==========================================
        // TURN 2: Move to Window, White Dress, Standing
        // ==========================================
        await RunTurnAsync(
            "Em đứng dậy đi ra cửa sổ ngắm cảnh đi.",
            "Được chứ, ta bước lại bên cửa sổ rồi.",
            new SceneStateDelta(PositionChange: "Beside Window", PoseChange: "Standing", ActionChange: "Walking toward window")
        );

        // ==========================================
        // TURN 3: Window, White Dress, Looking at User
        // ==========================================
        await RunTurnAsync(
            "Cảnh bên ngoài đẹp không em?",
            "Rất đẹp, nhưng ta thích nhìn anh hơn.",
            new SceneStateDelta(ActionChange: "Looking at user", ExpressionChange: "Warm affectionate smile")
        );

        // ==========================================
        // TURN 4: [EVENT] Change to Black Dress, Window, Standing
        // ==========================================
        await RunTurnAsync(
            "Tối nay đi dự tiệc nhé, em thay váy dạ hội đen đi.",
            "Chờ ta một chút nhé, ta đã thay chiếc váy đen quý phái rồi.",
            new SceneStateDelta(OutfitChange: "Black Evening Dress", Evidence: "Changed to Black Dress")
        );

        // ==========================================
        // TURN 5: Window, Black Dress, Sitting
        // ==========================================
        await RunTurnAsync(
            "Em ngồi nghỉ một lát bên bậu cửa sổ đi.",
            "Vâng, ta ngồi xuống bên cửa sổ ngắm trăng đây.",
            new SceneStateDelta(PoseChange: "Sitting beside window", ActionChange: "Resting gracefully")
        );

        // ==========================================
        // TURN 6: Move back to Sofa, Black Dress, Sitting
        // ==========================================
        await RunTurnAsync(
            "Gió lạnh rồi, em lại sofa ngồi với anh đi.",
            "Ta quay lại sofa ngồi cạnh anh đây.",
            new SceneStateDelta(PositionChange: "Sofa", PoseChange: "Sitting on sofa")
        );

        // ==========================================
        // TURN 7: [ITEM EVENT] Sofa, Black Dress, Holding Tea Cup
        // ==========================================
        await RunTurnAsync(
            "Uống tách trà ấm này cho đỡ lạnh nhé.",
            "Cảm ơn anh, tách trà ấm thật đấy.",
            new SceneStateDelta(HeldItemsChange: "Porcelain Tea Cup", ActionChange: "Sipping warm tea")
        );

        // ==========================================
        // TURN 8: [ITEM EVENT] Sofa, Black Dress, Drop/Finish Tea
        // ==========================================
        await RunTurnAsync(
            "Uống xong em đặt ly xuống bàn đi.",
            "Ta đặt tách trà xuống bàn rồi nè.",
            new SceneStateDelta(HeldItemsChange: "placed_down", ActionChange: "Relaxing with hands in lap")
        );

        // ==========================================
        // VERIFICATION OF ALL 8-TURN INVARIANTS
        // ==========================================
        Assert.Equal(8, capturedRequests.Count);

        // Verify Database Artifacts
        using (var verifyScope = scopeFactory.CreateScope())
        {
            var ctx = verifyScope.ServiceProvider.GetRequiredService<ProjectDbContext>();
            var artifacts = await ctx.SceneImages
                .Where(img => img.SessionId == sessionId)
                .OrderBy(img => img.SceneRevision)
                .ToListAsync();

            Assert.Equal(8, artifacts.Count);

            // Invariant 1: Master Identity Anchor is ALWAYS the Canonical Master Reference, NEVER replaced by previous frames!
            for (int i = 0; i < 8; i++)
            {
                Assert.Equal(canonicalIdentityUrl, capturedRequests[i].ReferenceImageUrl);
                Assert.Equal(canonicalIdentityUrl, artifacts[i].IdentityReferenceUrl);
            }

            // Invariant 2: Turn 1 has no previous frame; Turns 2-8 have exact predecessor artifact links!
            Assert.Null(capturedRequests[0].PreviousSceneImageUrl);
            Assert.Null(artifacts[0].PreviousSceneImageUrl);

            for (int r = 1; r < 8; r++)
            {
                var expectedPredecessorUrl = $"https://images.storage/elysia_frame_{r}.png";
                Assert.Equal(expectedPredecessorUrl, capturedRequests[r].PreviousSceneImageUrl);
                Assert.Equal(expectedPredecessorUrl, artifacts[r].PreviousSceneImageUrl);
            }

            // Invariant 3: Outfit progression (T1-T3 = White Dress; T4-T8 = Black Evening Dress)
            Assert.Contains("White Dress", capturedRequests[0].Prompt);
            Assert.Contains("White Dress", capturedRequests[1].Prompt);
            Assert.Contains("White Dress", capturedRequests[2].Prompt);
            Assert.Contains("Black Evening Dress", capturedRequests[3].Prompt);
            Assert.Contains("Black Evening Dress", capturedRequests[4].Prompt);
            Assert.Contains("Black Evening Dress", capturedRequests[5].Prompt);
            Assert.Contains("Black Evening Dress", capturedRequests[6].Prompt);
            Assert.Contains("Black Evening Dress", capturedRequests[7].Prompt);

            // Invariant 4: Position progression (T1: Sofa, T2-T5: Window, T6-T8: Sofa)
            Assert.Contains("Sofa", capturedRequests[0].Prompt);
            Assert.Contains("Beside Window", capturedRequests[1].Prompt);
            Assert.Contains("Beside Window", capturedRequests[2].Prompt);
            Assert.Contains("Beside Window", capturedRequests[3].Prompt);
            Assert.Contains("Beside Window", capturedRequests[4].Prompt);
            Assert.Contains("Sofa", capturedRequests[5].Prompt);
            Assert.Contains("Sofa", capturedRequests[6].Prompt);
            Assert.Contains("Sofa", capturedRequests[7].Prompt);

            // Invariant 5: Held item lifecycle (T7 = holding Porcelain Tea Cup, T8 = cleared/no item)
            Assert.Contains("holding Porcelain Tea Cup", capturedRequests[6].Prompt);
            Assert.DoesNotContain("holding Porcelain Tea Cup", capturedRequests[7].Prompt);
        }
    }

    private sealed class TestSceneTracker : ISceneStateTrackerService
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
}
