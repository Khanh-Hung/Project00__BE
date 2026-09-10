using System.Net;
using Application.DTOs;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.ImageGeneration;
using Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Project.Tests.GenerationProduction;

public class StyleDecouplingTests
{
    private readonly VisualPromptCompiler _compiler = new();

    private static VisualSnapshot CreateTestSnapshot(CharacterVisualIdentity? identity, string? model = null)
    {
        return VisualSnapshot.Create(
            turnId: Guid.NewGuid(),
            sessionId: Guid.NewGuid(),
            characterId: Guid.NewGuid(),
            sceneRevision: 1,
            visualIdentity: identity,
            sceneState: new SessionSceneState(
                CurrentLocation: "Office",
                CurrentPosition: "Desk",
                CurrentOutfit: "Business suit",
                CurrentTimeOfDay: "Daytime",
                HeldItems: "Pen",
                Atmosphere: "Professional",
                SceneRevision: 1,
                LastUpdatedAt: DateTime.UtcNow
            ),
            transientState: new TransientVisualState(
                Expression: "focused expression",
                Gaze: "looking at documents",
                Pose: "sitting upright"
            ),
            generationProfile: GenerationProfile.CreateDefault(model: model ?? "meinamix_meinaV11.safetensors")
        );
    }

    [Fact]
    public void Test1_ExplicitStyle_Realistic_IsPreserved_AndDoesNotAppendAnimeOrManhwa()
    {
        // Arrange
        var identity = new CharacterVisualIdentity(
            Presentation: GenderPresentation.Female,
            Hair: "brown hair",
            Eyes: "hazel eyes",
            Style: "Realistic",
            VisualStyle: VisualStyle.Realistic
        );
        var snapshot = CreateTestSnapshot(identity);

        // Act
        var positivePrompt = _compiler.CompileScenePrompt(snapshot);
        var negativePrompt = _compiler.CompileNegativePrompt(snapshot);

        // Assert
        Assert.Contains("photorealistic", positivePrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("realistic", positivePrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anime", positivePrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("manhwa", positivePrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pixiv", positivePrompt, StringComparison.OrdinalIgnoreCase);

        // Negative prompt contains anti-anime/cartoon tokens for realistic style
        Assert.Contains("anime", negativePrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Test2_ExplicitStyle_Anime_AppendsAnimeTokens_WithoutBeingHardcodedInvariant()
    {
        // Arrange
        var identity = new CharacterVisualIdentity(
            Presentation: GenderPresentation.Female,
            Hair: "silver hair",
            Eyes: "blue eyes",
            Style: "Anime",
            VisualStyle: VisualStyle.Anime
        );
        var snapshot = CreateTestSnapshot(identity);

        // Act
        var positivePrompt = _compiler.CompileScenePrompt(snapshot);
        var negativePrompt = _compiler.CompileNegativePrompt(snapshot);

        // Assert: anime style is explicitly included
        Assert.Contains("anime style", positivePrompt, StringComparison.OrdinalIgnoreCase);
        // Opposing photorealistic token is in negative prompt
        Assert.Contains("photorealistic", negativePrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Test3_StyleNeutral_Character_GeneratesCleanlyWithoutCrashing()
    {
        // Arrange: no explicit style provided
        var identity = new CharacterVisualIdentity(
            Presentation: GenderPresentation.Female,
            Hair: "black hair",
            Eyes: "dark eyes"
        );
        var snapshot = CreateTestSnapshot(identity);

        // Act
        var positivePrompt = _compiler.CompileScenePrompt(snapshot);
        var negativePrompt = _compiler.CompileNegativePrompt(snapshot);

        // Assert: produces valid prompt without crashing, contains base quality anchors, and does not force anime
        Assert.NotNull(positivePrompt);
        Assert.NotEmpty(positivePrompt);
        Assert.Contains("cinematic composition", positivePrompt);
        Assert.Contains("8k", positivePrompt);
        Assert.DoesNotContain("soft painterly anime aesthetic", positivePrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pixiv trending", positivePrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("manhwa", positivePrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Test4_RealisticStyle_DoesNotGetOverriddenByAnimeOrManhwa()
    {
        // Arrange: Character with explicit realistic style
        var identity = new CharacterVisualIdentity(
            Gender: "Woman",
            Face: "natural human face, light freckles",
            Hair: "wavy auburn hair",
            Eyes: "green eyes",
            Style: "Realistic"
        );
        var snapshot = CreateTestSnapshot(identity);

        // Act
        var positivePrompt = _compiler.CompileScenePrompt(snapshot);

        // Assert
        Assert.Contains("photorealistic", positivePrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anime", positivePrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("manhwa", positivePrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pixiv", positivePrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Test5_PollinationsProvider_DoesNotInjectForcedStyle_OrBanRealism()
    {
        // Arrange
        var capturedUrl = string.Empty;
        var fakeHandler = new FakeHttpMessageHandler((request) =>
        {
            capturedUrl = request.RequestUri?.ToString() ?? string.Empty;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[1500])
            };
        });
        var httpClient = new HttpClient(fakeHandler);
        var service = new PollinationsImageGenerationService(httpClient, NullLogger<PollinationsImageGenerationService>.Instance);

        var imageReq = new ImageGenerationRequest(
            Prompt: "realistic portrait of a woman, 35mm photograph, authentic skin texture",
            Width: 512,
            Height: 512,
            Seed: 42,
            NegativePrompt: "blurry, low quality"
        );

        // Act
        var result = await service.GenerateImageAsync(imageReq);

        // Assert
        var decodedUrl = Uri.UnescapeDataString(capturedUrl);
        Assert.Contains("realistic portrait of a woman", decodedUrl);
        Assert.DoesNotContain("otome isekai manhwa webtoon art style", decodedUrl);
        Assert.DoesNotContain("roxana anime aesthetic", decodedUrl);
        Assert.DoesNotContain("large bright luminous sparkling anime eyes", decodedUrl);
        // Ensure negative prompt does not ban realistic / photorealistic
        var negativePart = decodedUrl.Contains("negative_prompt=")
            ? decodedUrl.Substring(decodedUrl.IndexOf("negative_prompt=", StringComparison.Ordinal))
            : string.Empty;
        Assert.DoesNotContain("realistic", negativePart);
        Assert.DoesNotContain("photorealistic", negativePart);
    }

    [Fact]
    public void Test6_GenderInvariant_Male_DoesNotImplyKnightOrHandsomeOrAnime()
    {
        // Arrange
        var invariant = GenderPromptInvariant.Resolve(GenderPresentation.Male);

        // Act & Assert
        Assert.NotNull(invariant.PositiveTokens);
        Assert.Contains("1man", invariant.PositiveTokens);
        Assert.Contains("masculine face", invariant.PositiveTokens);

        // Invariant must NOT imply archetype, occupation, or aesthetic evaluation
        Assert.DoesNotContain("knight", invariant.PositiveTokens, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("handsome", invariant.PositiveTokens, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anime", invariant.PositiveTokens, StringComparison.OrdinalIgnoreCase);

        // Negative tokens must NOT contain anime girl
        Assert.NotNull(invariant.NegativeTokens);
        Assert.DoesNotContain("anime girl", invariant.NegativeTokens, StringComparison.OrdinalIgnoreCase);

        // Compiler test with generic male
        var identity = new CharacterVisualIdentity(
            Presentation: GenderPresentation.Male,
            Hair: "short black hair"
        );
        var snapshot = CreateTestSnapshot(identity);
        var prompt = _compiler.CompileScenePrompt(snapshot);

        Assert.Contains("1man", prompt);
        Assert.DoesNotContain("knight", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("handsome", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anime", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Test7_ModelIndependence_SameStyle_ProducesIdenticalPromptAcrossModels()
    {
        // Arrange: Realistic style across two different models (SD1.5 vs custom)
        var identity = new CharacterVisualIdentity(
            Presentation: GenderPresentation.Female,
            Hair: "blonde hair",
            Eyes: "blue eyes",
            Style: "Realistic",
            VisualStyle: VisualStyle.Realistic
        );

        var snapshotModelA = CreateTestSnapshot(identity, model: "model_sd15_standard.safetensors");
        var snapshotModelB = CreateTestSnapshot(identity, model: "model_custom_diffusion.safetensors");

        // Act
        var promptA = _compiler.CompileScenePrompt(snapshotModelA);
        var promptB = _compiler.CompileScenePrompt(snapshotModelB);

        var negA = _compiler.CompileNegativePrompt(snapshotModelA);
        var negB = _compiler.CompileNegativePrompt(snapshotModelB);

        // Assert: Prompt generation is completely decoupled from model resolution
        Assert.Equal(promptA, promptB);
        Assert.Equal(negA, negB);

        // Neither model causes realistic style to be overridden
        Assert.Contains("photorealistic", promptA);
        Assert.DoesNotContain("anime", promptA);
    }

    [Fact]
    public void Test8_CharacterExplicitStyle_Realistic_SurvivesIntoVisualSnapshot_OverridingDefaultStyle()
    {
        // Arrange: Character explicitly set to Realistic, but config DefaultStyle is Anime
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["AiProviders:ImageGeneration:DefaultStyle"] = "Anime"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var mapper = new SceneGenerationRequestMapper(configuration);
        var promptComposer = new ScenePromptComposer();

        var charId = Guid.NewGuid();
        var characterIdentity = new CharacterVisualIdentity(
            Presentation: GenderPresentation.Female,
            Hair: "Dark brown wavy",
            Eyes: "Amber",
            Skin: "Olive",
            ClothingStyle: "Casual leather jacket",
            Style: "Realistic",
            VisualStyle: VisualStyle.Realistic
        );

        var profile = new CharacterVisualProfile(
            characterId: charId,
            eyeColor: "Amber",
            hairColor: "Dark brown wavy",
            skinTone: "Olive",
            currentOutfit: "Casual leather jacket"
        );

        var spec = new SceneSpecification(
            characterId: charId,
            location: "Downtown Cafe",
            action: "Sipping coffee",
            sceneRevision: 1
        );

        var visualContext = new VisualContextResolutionResult(
            CharacterId: charId,
            VisualProfileVersion: 1,
            CanonicalIdentityReference: null,
            CurrentAppearance: profile,
            PredecessorVisualMemory: null,
            RelevantOlderMemories: Array.Empty<CharacterVisualMemory>(),
            TransitionType: SceneTransitionType.LocationTransition,
            SelectionSummary: "Test context",
            VisualIdentity: characterIdentity
        );

        var genProfile = GenerationProfile.CreateDefault("meinamix_meinaV11.safetensors");

        // Act
        var snapshot = mapper.MapToVisualSnapshot(spec, visualContext, genProfile, promptComposer);
        var compiledPrompt = _compiler.CompileScenePrompt(snapshot);
        var compiledNegative = _compiler.CompileNegativePrompt(snapshot);

        // Assert: Explicit style "Realistic" survived into snapshot, overriding default "Anime"
        Assert.NotNull(snapshot.VisualIdentity);
        Assert.Equal(VisualStyle.Realistic, snapshot.VisualIdentity.ResolvedStyle);
        Assert.Equal(VisualStyle.Realistic, snapshot.VisualIdentity.VisualStyle);

        // Worker prompt contains realistic style tokens rather than anime style tokens
        Assert.Contains("photorealistic", compiledPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anime", compiledPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pixiv", compiledPrompt, StringComparison.OrdinalIgnoreCase);

        // Negative prompt contains anti-anime tokens
        Assert.Contains("anime", compiledNegative, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Test9_CharacterWithoutExplicitStyle_InheritsConfiguredDefaultStyle()
    {
        // Arrange: Character has no explicit style, config DefaultStyle is Anime
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["AiProviders:ImageGeneration:DefaultStyle"] = "Anime"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var mapper = new SceneGenerationRequestMapper(configuration);
        var promptComposer = new ScenePromptComposer();

        var charId = Guid.NewGuid();
        // Character has no explicit style
        var characterIdentity = new CharacterVisualIdentity(
            Presentation: GenderPresentation.Female,
            Hair: "Blonde",
            Eyes: "Blue"
        );

        var profile = new CharacterVisualProfile(
            characterId: charId,
            eyeColor: "Blue",
            hairColor: "Blonde"
        );

        var spec = new SceneSpecification(
            characterId: charId,
            location: "Park",
            action: "Walking",
            sceneRevision: 1
        );

        var visualContext = new VisualContextResolutionResult(
            CharacterId: charId,
            VisualProfileVersion: 1,
            CanonicalIdentityReference: null,
            CurrentAppearance: profile,
            PredecessorVisualMemory: null,
            RelevantOlderMemories: Array.Empty<CharacterVisualMemory>(),
            TransitionType: SceneTransitionType.LocationTransition,
            SelectionSummary: "Test context",
            VisualIdentity: characterIdentity
        );

        var genProfile = GenerationProfile.CreateDefault("meinamix_meinaV11.safetensors");

        // Act
        var snapshot = mapper.MapToVisualSnapshot(spec, visualContext, genProfile, promptComposer);
        var compiledPrompt = _compiler.CompileScenePrompt(snapshot);
        var compiledNegative = _compiler.CompileNegativePrompt(snapshot);

        // Assert: Fallback to configured Anime style for backward compatibility
        Assert.NotNull(snapshot.VisualIdentity);
        Assert.Equal(VisualStyle.Anime, snapshot.VisualIdentity.ResolvedStyle);
        Assert.Equal(VisualStyle.Anime, snapshot.VisualIdentity.VisualStyle);

        // Worker prompt contains anime tokens and negative contains photorealistic
        Assert.Contains("anime style", compiledPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("photorealistic", compiledNegative, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Test10_SceneCompositionPipeline_PropagatesCharacterStyleFromDb_ToVisualSnapshot()
    {
        // Arrange: Setup in-memory SQLite DbContext with Character entity having explicit style
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(connection)
            .Options;

        using var db = new CoreDbContext(options);
        db.Database.EnsureCreated();

        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var character = new Character(
            name: "Kaelen",
            title: "Wanderer",
            avatarUrl: "https://cdn.project00.ai/kaelen.png",
            personalityPrompt: "Stoic",
            greeting: "Hello",
            category: "Fantasy"
        );
        typeof(Character).GetProperty("Id")!.SetValue(character, charId);
        typeof(Character).GetProperty("VisualIdentity")!.SetValue(character, new CharacterVisualIdentity(
            Hair: "Black",
            Eyes: "Grey",
            Style: "Realistic",
            VisualStyle: VisualStyle.Realistic
        ));

        var session = new ChatSession(charId, Guid.NewGuid(), "Roleplay");
        typeof(ChatSession).GetProperty("Id")!.SetValue(session, sessionId);

        db.Characters.Add(character);
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        var pipeline = Tests.SceneCompositionTestHelper.CreatePipeline(db);
        var intent = new SceneIntent(
            characterId: charId,
            locationHint: "Mountain pass",
            actionHint: "Looking at the horizon",
            sessionId: sessionId
        );
        var genProfile = GenerationProfile.CreateDefault("meinamix_meinaV11.safetensors");

        // Act: Execute the complete pipeline
        var result = await pipeline.ExecuteAsync(intent, genProfile, sceneRevision: 1);

        // Assert: Frozen snapshot preserved character style from DB
        Assert.NotNull(result.VisualSnapshot);
        Assert.NotNull(result.VisualSnapshot.VisualIdentity);
        Assert.Equal(VisualStyle.Realistic, result.VisualSnapshot.VisualIdentity.ResolvedStyle);
        Assert.Equal(VisualStyle.Realistic, result.VisualSnapshot.VisualIdentity.VisualStyle);

        var prompt = _compiler.CompileScenePrompt(result.VisualSnapshot);
        Assert.Contains("photorealistic", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anime", prompt, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
