using Application.DTOs;
using Application.Interfaces;
using Application.Services;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Infrastructure.Services.Scene;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.SceneComposition;

public sealed class SceneToGenerationIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public SceneToGenerationIntegrationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new ProjectDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    public async Task FullFlow_ChatIntent_ToSceneComposition_ToGenerationPipeline_ExecutesSuccessfully()
    {
        await using var db = new ProjectDbContext(_options);
        var dateTimeProvider = new SystemDateTimeProvider();

        // 1. Setup Architecture Services & Readers
        var profileService = new CharacterVisualProfileService(db, NullLogger<CharacterVisualProfileService>.Instance);
        var referenceService = new CharacterVisualReferenceService(db, profileService, NullLogger<CharacterVisualReferenceService>.Instance);

        var profileReader = new CharacterVisualProfileReader(db);
        var memoryReader = new VisualMemoryReader(db);
        var canonicalReader = new CanonicalReferenceReader(db);
        var previousSceneReader = new PreviousSceneReader(db);

        var contextFactory = new SceneCompositionContextFactory(
            profileReader, canonicalReader, memoryReader, previousSceneReader,
            NullLogger<SceneCompositionContextFactory>.Instance
        );

        var composer = new SceneComposer(NullLogger<SceneComposer>.Instance);
        var visualContextResolver = new VisualContextResolver(NullLogger<VisualContextResolver>.Instance);
        var promptComposer = new ScenePromptComposer();
        var requestMapper = new SceneGenerationRequestMapper();

        var pipelineService = new SceneCompositionPipelineService(
            contextFactory,
            composer,
            visualContextResolver,
            promptComposer,
            requestMapper,
            NullLogger<SceneCompositionPipelineService>.Instance
        );

        var lyraId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        // 2. Seed Character Visual Profile (Lyra) and Canonical Reference
        var profile = await profileService.CreateProfileAsync(
            characterId: lyraId,
            eyeColor: "Deep Violet",
            hairColor: "Silver Lilac",
            skinTone: "Porcelain",
            bodyIdentity: "Slender scholar",
            currentOutfit: "Emerald Academic Robes"
        );

        var canonicalRef = await referenceService.RegisterReferenceAsync(new RegisterVisualReferenceRequest(
            CharacterId: lyraId,
            ReferenceUrl: "https://cdn.project00.ai/lyra_canonical.png",
            IsCanonical: true,
            Type: VisualReferenceType.Canonical
        ));

        // 3. User message: "Lyra ngồi đọc sách trong thư viện lúc trời mưa."
        var intent = new SceneIntent(
            characterId: lyraId,
            locationHint: "Thư viện cổ kính",
            actionHint: "Ngồi đọc cuốn sách cổ",
            weatherHint: "Trời mưa rả rích bên ngoài cửa sổ kính",
            environmentHint: "Những kệ sách gỗ cao chạm trần, bàn gỗ sồi và cửa sổ kính đọng nước mưa",
            objectHints: new[] { "Cuốn sách cổ bọc da", "Ngọn nến lung linh" },
            sessionId: sessionId,
            turnId: turnId
        );

        var genProfile = new GenerationProfile(
            Seed: 99999L,
            Workflow: "VisualIdentity",
            WorkflowVersion: 1,
            Width: 1024,
            Height: 1024
        );

        // 4. Execute Full Scene Composition Pipeline
        var pipelineResult = await pipelineService.ExecuteAsync(intent, genProfile, sceneRevision: 1);

        Assert.NotNull(pipelineResult);
        var spec = pipelineResult.SceneSpecification;
        var visualContext = pipelineResult.VisualContext;
        var prompt = pipelineResult.ScenePrompt;
        var snapshot = pipelineResult.VisualSnapshot;

        // Verify SceneSpecification
        Assert.Equal(lyraId, spec.CharacterId);
        Assert.Equal("Thư viện cổ kính", spec.Location);
        Assert.Equal("Ngồi đọc cuốn sách cổ", spec.Action);
        Assert.Equal("seated naturally", spec.Pose); // Inferred from "đọc" / "ngồi"
        Assert.Equal("Trời mưa rả rích bên ngoài cửa sổ kính", spec.Weather);
        Assert.NotNull(spec.SceneFingerprint);

        // Verify Canonical Identity Dominance
        Assert.NotNull(visualContext.CanonicalIdentityReference);
        Assert.Equal(canonicalRef.ReferenceUrl, visualContext.CanonicalIdentityReference.ReferenceUrl);

        // Verify Structured Prompt Compilation
        Assert.Contains("[Character: Silver Lilac hair, Deep Violet eyes", prompt.PositivePrompt);
        Assert.Contains("[Action: Ngồi đọc cuốn sách cổ]", prompt.PositivePrompt);
        Assert.Contains("[Pose: seated naturally]", prompt.PositivePrompt);
        Assert.Contains("[Outfit: Emerald Academic Robes]", prompt.PositivePrompt);
        Assert.Contains("[Environment: Những kệ sách gỗ cao chạm trần", prompt.PositivePrompt);
        Assert.Contains("[Props: Cuốn sách cổ bọc da, Ngọn nến lung linh]", prompt.PositivePrompt);
        Assert.Contains("[Weather: Trời mưa rả rích bên ngoài cửa sổ kính]", prompt.PositivePrompt);

        // Verify Snapshot Compatibility with Generation Engine
        Assert.NotNull(snapshot);
        Assert.Equal(turnId, snapshot.TurnId);
        Assert.Equal(sessionId, snapshot.SessionId);
        Assert.Equal(lyraId, snapshot.CharacterId);
        Assert.Equal(1, snapshot.SceneRevision);
        Assert.Equal(canonicalRef.ReferenceUrl, snapshot.IdentityReferenceUrl);

        // Persist SceneSpecification
        db.SceneSpecifications.Add(spec);
        await db.SaveChangesAsync();

        // 5. Verify DB Persistence & Invariants
        var persistedSpec = await db.SceneSpecifications.FirstOrDefaultAsync(s => s.Id == spec.Id);
        Assert.NotNull(persistedSpec);
        Assert.Equal(spec.SceneFingerprint, persistedSpec.SceneFingerprint);
        Assert.Equal("Thư viện cổ kính", persistedSpec.Environment.Location);
        Assert.Equal(2, persistedSpec.Environment.Props.Length);

        // Character profile core identity remains unmutated (Strict Identity Isolation)
        var profileAfter = await db.CharacterVisualProfiles.FirstAsync(p => p.CharacterId == lyraId);
        Assert.Equal("Deep Violet", profileAfter.EyeColor);
        Assert.Equal("Silver Lilac", profileAfter.HairColor);
        Assert.Equal(2, profileAfter.VisualVersion);
    }
}
