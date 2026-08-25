using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.ImageGeneration.ComfyUI;
using System.Text.Json;
using Xunit;

namespace Tests;

public sealed class VisualIdentityFidelityBenchmarkTests
{
    private readonly VisualPromptCompiler _compiler = new();
    private readonly VisualIdentityWorkflowV1Builder _workflowBuilder = new();

    private static readonly (string Name, CharacterVisualIdentity Identity, string DefaultOutfit)[] Archetypes = new[]
    {
        (
            "Silver Priestess (Seraphina)",
            new CharacterVisualIdentity(
                Gender: "Female",
                Face: "delicate elegant face, sharp jawline",
                Hair: "long silver hair in neat updo",
                Eyes: "sharp emerald green eyes",
                Skin: "porcelain white skin",
                Body: "slender graceful build",
                ClothingStyle: "white gown with dark green corset",
                Accessories: "golden gear hair ornament, jade earrings",
                CanonicalReferenceUrl: "https://cloud.storage/seraphina_face_crop.png"
            ),
            "white gown with dark green corset"
        ),
        (
            "Blonde Mage (Elysia)",
            new CharacterVisualIdentity(
                Gender: "Female",
                Face: "youthful cute face, expressive eyes",
                Hair: "golden blonde twin tails with black ribbons",
                Eyes: "vibrant sapphire blue eyes",
                Skin: "fair skin",
                Body: "petite build",
                ClothingStyle: "black gothic lolita dress",
                Accessories: "black lace choker, magical silver brooch",
                CanonicalReferenceUrl: "https://cloud.storage/elysia_face_crop.png"
            ),
            "black gothic lolita dress"
        ),
        (
            "Raven Knight (Kaelen)",
            new CharacterVisualIdentity(
                Gender: "Male",
                Face: "chiseled handsome face, determined jawline",
                Hair: "jet black short spiky hair",
                Eyes: "intense amethyst violet eyes",
                Skin: "lightly tanned skin",
                Body: "athletic muscular build",
                ClothingStyle: "dark leather knight tunic with silver pauldrons",
                Accessories: "silver browband, leather bracers",
                CanonicalReferenceUrl: "https://cloud.storage/kaelen_face_crop.png"
            ),
            "dark leather knight tunic with silver pauldrons"
        ),
        (
            "Pastel Healer (Lunaria)",
            new CharacterVisualIdentity(
                Gender: "Female",
                Face: "soft gentle face, warm smile",
                Hair: "pastel pink wavy hair falling over shoulders",
                Eyes: "warm golden amber eyes",
                Skin: "soft pale skin",
                Body: "slender feminine build",
                ClothingStyle: "flowing white and lavender silk robe",
                Accessories: "cherry blossom hairpin, crystal pendant",
                CanonicalReferenceUrl: "https://cloud.storage/lunaria_face_crop.png"
            ),
            "flowing white and lavender silk robe"
        ),
        (
            "Crimson Assassin (Scarlet)",
            new CharacterVisualIdentity(
                Gender: "Female",
                Face: "sharp fierce face, calculating gaze",
                Hair: "deep crimson red high ponytail",
                Eyes: "golden yellow slit eyes",
                Skin: "pale ivory skin",
                Body: "toned agile build",
                ClothingStyle: "fitted black leather stealth outfit with crimson trims",
                Accessories: "leather neck choker, throwing knife holster",
                CanonicalReferenceUrl: "https://cloud.storage/scarlet_face_crop.png"
            ),
            "fitted black leather stealth outfit with crimson trims"
        )
    };

    /// <summary>
    /// Contract regression test verifying that explicit IPAdapter parameter overrides (e.g. 0.55 / 0.75)
    /// in ParametersJson are respected across multiple seeds and character archetypes,
    /// while physical prompt invariants are strictly preserved.
    /// (Production default parameters 0.65 / 0.85 are verified in ComfyUIWorkflowBuilderTests).
    /// </summary>
    [Theory]
    [InlineData(0)] // Archetype 1: Silver Priestess
    [InlineData(1)] // Archetype 2: Blonde Mage
    [InlineData(2)] // Archetype 3: Raven Knight
    [InlineData(3)] // Archetype 4: Pastel Healer
    [InlineData(4)] // Archetype 5: Crimson Assassin
    public void WorkflowBuilder_Respects_Explicit_IPAdapter_Override_Across_5_Archetypes_And_10_Seeds(int archetypeIndex)
    {
        var (name, identity, defaultOutfit) = Archetypes[archetypeIndex];

        var sceneState = new SessionSceneState(
            CurrentLocation: "Sanctuary Hall",
            CurrentPosition: "Central Area",
            CurrentOutfit: defaultOutfit,
            CurrentTimeOfDay: "Daytime",
            HeldItems: null,
            Atmosphere: "Calm",
            SceneRevision: 1,
            LastUpdatedAt: DateTime.UtcNow
        );

        var transient = new TransientVisualState(
            Pose: "Standing gracefully",
            Action: "Observing surroundings",
            Expression: "Calm observant gaze"
        );

        var sceneDesc = new VisualSceneDescription(
            shotType: "medium shot",
            cameraAngle: "slight 3/4 turn, eye level",
            subjectPlacement: "centered",
            detailedAction: "standing gracefully observing surroundings",
            detailedEnvironment: "grand sanctuary hall with marble pillars",
            lightingStyle: "soft natural daylight streaming from tall windows",
            atmosphere: "calm and serene"
        );

        var rng = new Random(42 + archetypeIndex);

        // Test 10 distinct seeds for this archetype
        for (int i = 0; i < 10; i++)
        {
            var seed = rng.NextInt64(100000, 999999999);
            var profile = GenerationProfile.CreateDefault(
                seed: seed,
                workflow: "VisualIdentity",
                workflowVersion: 1,
                parametersJson: "{\"ipAdapter\":{\"weight\":0.55,\"endAt\":0.75}}"
            );

            var snapshot = VisualSnapshot.Create(
                turnId: Guid.NewGuid(),
                sessionId: Guid.NewGuid(),
                characterId: Guid.NewGuid(),
                sceneRevision: 1,
                visualIdentity: identity,
                sceneState: sceneState,
                transientState: transient,
                generationProfile: profile,
                sceneDescription: sceneDesc
            );

            // 1. Compile prompt & verify Physical Identity Invariants
            var prompt = _compiler.CompileScenePrompt(snapshot);
            Assert.NotEmpty(prompt);

            // Verify Gender tag
            if (identity.Gender == "Male")
                Assert.Contains("1boy", prompt);
            else
                Assert.Contains("1girl", prompt);

            // Verify Hair & Eye color invariants
            Assert.Contains(identity.Hair!, prompt);
            Assert.Contains(identity.Eyes!, prompt);

            // Verify Persistent Outfit & Location
            Assert.Contains($"wearing {defaultOutfit}", prompt);
            Assert.Contains("Sanctuary Hall", prompt);

            // Verify Camera Framing Stability
            Assert.Contains("medium shot", prompt);
            Assert.Contains("slight 3/4 turn", prompt);

            // 2. Build ComfyUI Workflow Graph & verify IP-Adapter Invariants
            var imageReq = ImageGenerationRequest.FromSnapshot(snapshot, prompt);
            var graph = _workflowBuilder.BuildWorkflow(imageReq, "canonical_face_crop.png");

            Assert.NotNull(graph);
            Assert.True(graph.ContainsKey("10")); // IPAdapterAdvanced node

            var ipAdapterNode = Assert.IsAssignableFrom<Dictionary<string, object>>(graph["10"]);
            Assert.Equal("IPAdapterAdvanced", ipAdapterNode["class_type"]);

            var inputs = Assert.IsAssignableFrom<Dictionary<string, object>>(ipAdapterNode["inputs"]);
            Assert.Equal("K+V", inputs["embeds_scaling"]);
            Assert.Equal(0.55, (double)inputs["weight"], precision: 2);
            Assert.Equal(0.75, (double)inputs["end_at"], precision: 2);

            // Verify KSampler uses the exact seed
            var ksamplerNode = Assert.IsAssignableFrom<Dictionary<string, object>>(graph["3"]);
            var ksamplerInputs = Assert.IsAssignableFrom<Dictionary<string, object>>(ksamplerNode["inputs"]);
            Assert.Equal(seed, (long)ksamplerInputs["seed"]);
        }
    }

    [Fact]
    public void Benchmark_Deterministic_Replay_Produces_Identical_Workflow_Payload()
    {
        var (_, identity, defaultOutfit) = Archetypes[0]; // Seraphina

        var snapshot = VisualSnapshot.Create(
            turnId: Guid.NewGuid(),
            sessionId: Guid.NewGuid(),
            characterId: Guid.NewGuid(),
            sceneRevision: 1,
            visualIdentity: identity,
            sceneState: new SessionSceneState(CurrentLocation: "Aetheria Workshop", CurrentOutfit: defaultOutfit),
            transientState: new TransientVisualState(Pose: "Standing", Expression: "Gentle smile"),
            generationProfile: GenerationProfile.CreateDefault(seed: 777888999),
            sceneDescription: new VisualSceneDescription(shotType: "medium shot", cameraAngle: "slight 3/4 turn")
        );

        var prompt1 = _compiler.CompileScenePrompt(snapshot);
        var prompt2 = _compiler.CompileScenePrompt(snapshot);
        Assert.Equal(prompt1, prompt2);

        var req1 = ImageGenerationRequest.FromSnapshot(snapshot, prompt1);
        var req2 = ImageGenerationRequest.FromSnapshot(snapshot, prompt2);

        var graph1 = _workflowBuilder.BuildWorkflow(req1, "seraphina_face_crop.png");
        var graph2 = _workflowBuilder.BuildWorkflow(req2, "seraphina_face_crop.png");

        var json1 = JsonSerializer.Serialize(graph1);
        var json2 = JsonSerializer.Serialize(graph2);

        Assert.Equal(json1, json2);
    }
}
