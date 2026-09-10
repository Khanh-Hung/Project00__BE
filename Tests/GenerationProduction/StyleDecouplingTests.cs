using System.Net;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.ImageGeneration;
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
