using System.Text.Json;
using Application.Abstractions.Data;
using Application.Common;
using Application.DTOs;
using Application.Enums;
using Application.Interfaces;
using Application.Services;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests;

public sealed class ProductionBenchmarkCompilerExporter
{
    public sealed class ExportableTurnRequest
    {
        public string CharacterId { get; set; } = string.Empty;
        public string CharacterName { get; set; } = string.Empty;
        public string Gender { get; set; } = string.Empty;
        public int Turn { get; set; }
        public string Location { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public bool IsTransition { get; set; }
        public long Seed { get; set; }
        public string CompiledPrompt { get; set; } = string.Empty;
        public string CompiledNegative { get; set; } = string.Empty;
        public string IdentityReferenceUrl { get; set; } = string.Empty;
        public string? PreviousSceneImageUrl { get; set; }
        public double Slot2Weight { get; set; }
        public double Slot2EndAt { get; set; }
        public string WeightType { get; set; } = "style transfer";
        public bool Slot2Active { get; set; }
        public string TargetActionPrompt { get; set; } = string.Empty;
        public List<string> NegativeActionPrompts { get; set; } = new();
    }

    public static async Task<List<ExportableTurnRequest>> GenerateAllAuthoritativeRequestsAsync()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ProjectDbContext(options);
        var unitOfWork = new UnitOfWork(db);
        var compiler = new VisualPromptCompiler();
        var profileProvider = new VisualGenerationProfileProvider();

        var exportedList = new List<ExportableTurnRequest>();

        // 1. LYRA SCENARIO
        var lyraHorns = new SignatureFeature(
            Name: "DragonHorns",
            PositiveTokens: "sharp black dragon horns with glowing red accents on head",
            NegativeTokens: "deformed horns, missing horns, extra horns, asymmetrical malformed horns",
            Importance: FeatureImportance.Critical,
            Persistence: FeaturePersistence.EveryTurn
        );
        var lyraIdentity = new CharacterVisualIdentity(
            Presentation: GenderPresentation.Female,
            Hair: "long silver white hair",
            Eyes: "striking crimson red eyes",
            Skin: "delicate porcelain skin",
            ClothingStyle: "white and gold silk priestess dress",
            CanonicalReferenceUrl: "Lyra_tight_face.png",
            SignatureFeatures: new[] { lyraHorns }
        );
        var lyra = new Character("Lyra", "Silver Dragon Saintess", "Lyra_tight_face.png", "Gentle", "Greetings", "Anime", visualIdentity: lyraIdentity);
        await db.Characters.AddAsync(lyra);

        var lyraSession = new ChatSession(lyra.Id, Guid.NewGuid(), "Lyra Benchmark");
        await db.ChatSessions.AddAsync(lyraSession);
        await db.SaveChangesAsync();

        var lyraTurns = new[]
        {
            (1, "Sanctuary (Standing Window)", "Sanctuary", false, "standing", 100001L, "an anime girl standing beside an arched window", new[] {"an anime girl sitting on a chair", "an anime girl kneeling in prayer", "an anime girl lying on the floor"}, "standing beside grand arched window in sunlit sanctuary hall, soft golden daylight, medium shot, slight 3/4 turn"),
            (2, "Sanctuary (Walking Altar)", "Sanctuary", false, "walking", 100002L, "an anime girl walking along an aisle holding a book", new[] {"an anime girl sitting down", "an anime girl sleeping", "an anime girl lying down"}, "walking along marble aisle towards grand altar, holding ancient sacred scripture, streaming sunlight, medium shot"),
            (3, "Sanctuary (Kneeling Prayer)", "Sanctuary", false, "kneeling", 100003L, "an anime girl kneeling in prayer before an altar hands clasped", new[] {"an anime girl standing tall", "an anime girl running fast", "an anime girl dancing"}, "kneeling before golden altar in prayer, hands clasped, soft divine glowing aura, medium shot"),
            (4, "Sanctuary (Smiling Turn)", "Sanctuary", false, "standing/smiling", 100004L, "an anime girl standing and smiling warmly looking at viewer", new[] {"an anime girl crying sadly", "an anime girl sleeping", "an anime girl lying down"}, "standing gracefully near altar, looking towards viewer with a gentle affectionate smile, soft ambient light, medium shot"),
            (5, "Library (Sitting Tea)", "Library", true, "sitting", 100005L, "an anime girl sitting at a wooden table drinking tea", new[] {"an anime girl standing outside", "an anime girl running", "an anime girl lying on bed"}, "wearing silk traveler cloak, sitting at wooden table in cozy library, holding warm ceramic teacup, warm ambient indoor light, medium shot"),
            (6, "Library (Reading Grimoire)", "Library", false, "reading/leaning", 100006L, "an anime girl leaning over an open book reading a grimoire", new[] {"an anime girl standing straight", "an anime girl dancing actively", "an anime girl sleeping"}, "wearing silk traveler cloak, leaning over large open ancient grimoire on library desk, pointing at glowing magical runes, focused expression, medium shot"),
            (7, "Balcony (Twilight Walk)", "Balcony", true, "walking", 100007L, "an anime girl walking on an outdoor stone balcony at twilight", new[] {"an anime girl sitting inside a room", "an anime girl sleeping in bed"}, "wearing silk traveler cloak, walking out onto palace stone balcony overlooking kingdom at dusk, gentle twilight breeze blowing hair, medium shot"),
            (8, "Balcony (Night Stars)", "Balcony", false, "leaning/gazing", 100008L, "an anime girl leaning against a stone balustrade looking at distance", new[] {"an anime girl running indoors", "an anime girl sitting on a couch"}, "wearing silk traveler cloak, leaning against carved stone balustrade, looking into distance with contemplative smile, soft sunset horizon glowing in eyes, medium shot")
        };

        await ProcessCharacterScenarioAsync(db, unitOfWork, compiler, profileProvider, lyra, lyraSession, "character_01_lyra", lyraTurns, exportedList);

        // 2. ELYSIA SCENARIO
        var elysiaEars = new SignatureFeature(
            Name: "ElfEars",
            PositiveTokens: "long elegant pointed elf ears",
            NegativeTokens: "human round ears, missing ears, extra ears",
            Importance: FeatureImportance.Critical,
            Persistence: FeaturePersistence.EveryTurn
        );
        var elysiaIdentity = new CharacterVisualIdentity(
            Presentation: GenderPresentation.Female,
            Hair: "wavy pastel pink hair",
            Eyes: "crystal clear sapphire blue eyes",
            Skin: "fair skin",
            ClothingStyle: "scholarly white and gold academy robes",
            CanonicalReferenceUrl: "Elysia_tight_face.png",
            SignatureFeatures: new[] { elysiaEars }
        );
        var elysia = new Character("Elysia", "High Elf Scholar", "Elysia_tight_face.png", "Gentle", "Hello", "Anime", visualIdentity: elysiaIdentity);
        await db.Characters.AddAsync(elysia);

        var elysiaSession = new ChatSession(elysia.Id, Guid.NewGuid(), "Elysia Benchmark");
        await db.ChatSessions.AddAsync(elysiaSession);
        await db.SaveChangesAsync();

        var elysiaTurns = new[]
        {
            (1, "Academy (Standing Desk)", "Academy", false, "standing", 200001L, "an elf girl standing in an academy classroom beside a desk", new[] {"an elf girl sitting on floor", "an elf girl running", "an elf girl sleeping"}, "standing beside polished mahogany desk in grand academy lecture hall, morning sunlight through tall windows, medium shot"),
            (2, "Academy (Walking Shelf)", "Academy", false, "walking", 200002L, "an elf girl walking between tall bookshelves reaching for a book", new[] {"an elf girl sitting down", "an elf girl sleeping in bed", "an elf girl dancing"}, "walking between towering wooden bookshelves, reaching for leather-bound tome on high shelf, soft dust motes in sunlight, medium shot"),
            (3, "Academy (Sitting Desk)", "Academy", false, "sitting", 200003L, "an elf girl sitting at a desk writing on parchment with a quill", new[] {"an elf girl standing outside", "an elf girl running fast", "an elf girl lying on grass"}, "sitting at study desk with quill in hand, writing notes on parchment paper, thoughtful focused expression, warm lamp light, medium shot"),
            (4, "Academy (Kneeling Sorting)", "Academy", false, "kneeling", 200004L, "an elf girl kneeling on the carpet organizing ancient scrolls", new[] {"an elf girl standing tall", "an elf girl sitting on chair", "an elf girl running"}, "kneeling on ornate Persian carpet sorting ancient glowing scrolls, smiling curiously, warm library ambiance, medium shot"),
            (5, "Botanical Garden (Standing Flower)", "Botanical Garden", true, "standing/smiling", 200005L, "an elf girl standing among glowing flowers in a greenhouse", new[] {"an elf girl sitting in dark room", "an elf girl sleeping", "an elf girl running in street"}, "wearing casual floral scholar dress, standing among luminescent magical flowers in botanical glasshouse, holding crystal magnifying glass, bright cheerful smile, dappled sunbeams, medium shot"),
            (6, "Botanical Garden (Sitting Bench)", "Botanical Garden", false, "sitting", 200006L, "an elf girl sitting on a garden bench sketching botanical plants", new[] {"an elf girl standing on stage", "an elf girl running", "an elf girl swimming"}, "wearing casual floral scholar dress, sitting on white stone garden bench with sketchbook in lap, sketching exotic flora, peaceful serene expression, gentle floral breeze, medium shot"),
            (7, "Observatory (Standing Telescope)", "Observatory", true, "standing/leaning", 200007L, "an elf girl leaning against a brass astronomical telescope at night", new[] {"an elf girl sitting in bright sun", "an elf girl running outdoors"}, "wearing deep blue velvet star cloak, standing beside massive brass astronomical telescope inside domed observatory at midnight, starlight and nebula glow reflecting on face, medium shot"),
            (8, "Observatory (Looking Sky)", "Observatory", false, "standing/smiling", 200008L, "an elf girl looking up at the starry night sky with wonder", new[] {"an elf girl looking down sadly", "an elf girl sleeping on sofa"}, "wearing deep blue velvet star cloak, standing under open glass dome of observatory, gazing up at cosmic constellations with joyful sparkling eyes, celestial moonlight, medium shot")
        };

        await ProcessCharacterScenarioAsync(db, unitOfWork, compiler, profileProvider, elysia, elysiaSession, "character_02_elysia", elysiaTurns, exportedList);

        // 3. VALERIUS SCENARIO
        var valeriusArmor = new SignatureFeature(
            Name: "KnightArmor",
            PositiveTokens: "dark steel knight commander armor with silver trims",
            NegativeTokens: "casual t-shirt, swimsuit",
            Importance: FeatureImportance.Critical,
            Persistence: FeaturePersistence.EveryTurn
        );
        var valeriusIdentity = new CharacterVisualIdentity(
            Presentation: GenderPresentation.Male,
            Hair: "short textured jet black hair",
            Eyes: "sharp piercing golden amber eyes",
            Face: "chiseled handsome jawline",
            ClothingStyle: "dark steel knight commander armor with silver trims",
            CanonicalReferenceUrl: "Valerius_tight_face.png",
            SignatureFeatures: new[] { valeriusArmor }
        );
        var valerius = new Character("Valerius", "Shadow Knight Commander", "Valerius_tight_face.png", "Loyal", "Greetings", "Knight", visualIdentity: valeriusIdentity);
        await db.Characters.AddAsync(valerius);

        var valeriusSession = new ChatSession(valerius.Id, Guid.NewGuid(), "Valerius Benchmark");
        await db.ChatSessions.AddAsync(valeriusSession);
        await db.SaveChangesAsync();

        var valeriusTurns = new[]
        {
            (1, "Armory (Inspecting Blade)", "Armory", false, "standing", 300001L, "an anime knight man standing holding a sheathed sword", new[] {"an anime knight sitting down", "an anime knight sleeping", "an anime knight lying on ground"}, "standing resolutely in fortress armory holding sheathed longsword, torchlight reflections on metal, medium shot, slight 3/4 turn"),
            (2, "Armory (Polishing Armor)", "Armory", false, "sitting", 300002L, "an anime knight man sitting at a workbench maintaining equipment", new[] {"an anime knight running outdoors", "an anime knight dancing", "an anime knight swimming"}, "sitting at heavy wooden armory workbench, cleaning gauntlet with cloth, focused determined expression, warm forge glow, medium shot"),
            (3, "War Room (Studying Map)", "War Room", true, "leaning", 300003L, "an anime knight man leaning over a war map planning strategy", new[] {"an anime knight sleeping in bed", "an anime knight dancing", "an anime knight sitting on floor"}, "wearing dark military commander tunic with silver cloak, leaning forward over large parchment battle map on stone table in war room, strategic intense gaze, flickering candlelight, medium shot"),
            (4, "War Room (Standing Briefing)", "War Room", false, "standing", 300004L, "an anime knight man standing giving a briefing", new[] {"an anime knight sitting on couch", "an anime knight sleeping", "an anime knight lying down"}, "wearing dark military commander tunic with silver cloak, standing authoritatively beside war table addressing unseen officers, confident leader expression, warm hearth lighting, medium shot"),
            (5, "War Room (Wine Cup Rest)", "War Room", false, "sitting", 300005L, "an anime knight man sitting in a heavy chair holding a metal goblet", new[] {"an anime knight running actively", "an anime knight jumping", "an anime knight lying on ground"}, "wearing dark military commander tunic with silver cloak, sitting back in high-backed wooden chair holding silver wine goblet, pensive stern expression, dim firelight ambiance, medium shot"),
            (6, "Battlements (Gazing Kingdom)", "Battlements", true, "standing/leaning", 300006L, "an anime knight man standing on fortress battlements looking at the kingdom", new[] {"an anime knight sitting inside room", "an anime knight sleeping in bed"}, "wearing full plate armor with dark battle cloak, standing atop windy stone fortress battlements, leaning against parapet looking over misty valley, cold night wind blowing cloak, dramatic moonlight, medium shot"),
            (7, "Battlements (Night Patrol Walk)", "Battlements", false, "walking", 300007L, "an anime knight man walking along the ramparts carrying a torch", new[] {"an anime knight sitting at desk", "an anime knight sleeping", "an anime knight dancing"}, "wearing full plate armor with dark battle cloak, walking along stone ramparts on night patrol carrying iron torch, vigilant sharp expression, torchlight casting long shadows, medium shot"),
            (8, "Battlements (Salute to Dawn)", "Battlements", false, "standing", 300008L, "an anime knight man standing saluting the rising sun with drawn sword", new[] {"an anime knight sitting down", "an anime knight sleeping", "an anime knight lying in bed"}, "wearing full plate armor with dark battle cloak, standing tall on highest fortress tower, raising broadsword in solemn military salute towards rising dawn sun, golden morning light cresting horizon, medium shot")
        };

        await ProcessCharacterScenarioAsync(db, unitOfWork, compiler, profileProvider, valerius, valeriusSession, "character_03_valerius", valeriusTurns, exportedList);

        return exportedList;
    }

    [Fact]
    public async Task Export_Authoritative_Production_Generation_Requests()
    {
        var exportedList = await GenerateAllAuthoritativeRequestsAsync();

        var artifactsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "eval_artifacts_v23"));
        Directory.CreateDirectory(artifactsDir);
        var jsonPath = Path.Combine(artifactsDir, "authoritative_compiled_requests.json");
        var jsonContent = JsonSerializer.Serialize(exportedList, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(jsonPath, jsonContent);

        Assert.Equal(24, exportedList.Count);
        Assert.True(File.Exists(jsonPath));
    }

    [Fact]
    public async Task Assert_Committed_Authoritative_Requests_JSON_Is_Not_Stale()
    {
        var freshlyGenerated = await GenerateAllAuthoritativeRequestsAsync();

        var artifactsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "eval_artifacts_v23"));
        var jsonPath = Path.Combine(artifactsDir, "authoritative_compiled_requests.json");

        Assert.True(File.Exists(jsonPath), $"Committed JSON not found at: {jsonPath}");

        var committedJson = await File.ReadAllTextAsync(jsonPath);
        var committedList = JsonSerializer.Deserialize<List<ExportableTurnRequest>>(committedJson);

        Assert.NotNull(committedList);
        Assert.Equal(freshlyGenerated.Count, committedList.Count);

        for (int i = 0; i < freshlyGenerated.Count; i++)
        {
            var fresh = freshlyGenerated[i];
            var comm = committedList[i];

            Assert.Equal(fresh.CharacterId, comm.CharacterId);
            Assert.Equal(fresh.Turn, comm.Turn);
            Assert.Equal(fresh.CompiledPrompt, comm.CompiledPrompt);
            Assert.Equal(fresh.CompiledNegative, comm.CompiledNegative);
            Assert.Equal(fresh.IdentityReferenceUrl, comm.IdentityReferenceUrl);
            Assert.Equal(fresh.PreviousSceneImageUrl, comm.PreviousSceneImageUrl);
            Assert.Equal(fresh.Slot2Weight, comm.Slot2Weight, precision: 4);
            Assert.Equal(fresh.Slot2EndAt, comm.Slot2EndAt, precision: 4);
            Assert.Equal(fresh.WeightType, comm.WeightType);
            Assert.Equal(fresh.Slot2Active, comm.Slot2Active);
        }
    }

    private static async Task ProcessCharacterScenarioAsync(
        ProjectDbContext db,
        IUnitOfWork unitOfWork,
        IVisualPromptCompiler compiler,
        IVisualGenerationProfileProvider profileProvider,
        Character character,
        ChatSession session,
        string characterId,
        (int turn, string location, string room, bool isTransition, string action, long seed, string actionPrompt, string[] negActions, string basePrompt)[] turns,
        List<ExportableTurnRequest> exportedList)
    {
        var tracker = new DummySceneStateTracker();
        var resolver = new VisualStateResolver(unitOfWork, tracker, profileProvider, SceneCompositionTestHelper.CreatePipeline(db));

        for (int i = 0; i < turns.Length; i++)
        {
            var t = turns[i];
            tracker.NextDelta = new SceneStateDelta(
                LocationChange: t.room,
                ActionChange: t.action
            );

            var (sceneState, transientState, snapshot) = await resolver.ResolveTurnVisualStateAsync(
                character, session, $"Turn {t.turn} action", t.basePrompt, CharacterMood.Neutral, Guid.NewGuid());

            session.UpdateSceneState(sceneState);
            await db.SaveChangesAsync();

            // Commit scene image to DB to establish predecessor lineage for next turn
            var nextImageFilename = $"{characterId}_turn_{t.turn}_input.png";
            var sceneImage = new SceneImage(session.Id, character.Id, snapshot.TurnId, t.turn, nextImageFilename, t.basePrompt, isCurrent: true);
            await db.SceneImages.AddAsync(sceneImage);
            await db.SaveChangesAsync();

            var compiledPrompt = compiler.CompileScenePrompt(snapshot);
            var compiledNegative = compiler.CompileNegativePrompt(snapshot);

            using var doc = JsonDocument.Parse(snapshot.GenerationProfile.ParametersJson);
            var sc = doc.RootElement.GetProperty("sceneContinuity");
            double weight = sc.GetProperty("weight").GetDouble();
            double endAt = sc.GetProperty("endAt").GetDouble();
            string weightType = sc.TryGetProperty("weightType", out var wt) ? wt.GetString() ?? "style transfer" : "style transfer";
            bool isActive = weight > 0.0 && !string.IsNullOrWhiteSpace(snapshot.PreviousSceneImageUrl);

            exportedList.Add(new ExportableTurnRequest
            {
                CharacterId = characterId,
                CharacterName = character.Name,
                Gender = character.VisualIdentity?.ResolvedGender.ToString() ?? "Female",
                Turn = t.turn,
                Location = t.location,
                Action = t.action,
                IsTransition = t.isTransition,
                Seed = t.seed,
                CompiledPrompt = compiledPrompt,
                CompiledNegative = compiledNegative,
                IdentityReferenceUrl = snapshot.IdentityReferenceUrl ?? character.AvatarUrl,
                PreviousSceneImageUrl = snapshot.PreviousSceneImageUrl,
                Slot2Weight = weight,
                Slot2EndAt = endAt,
                WeightType = weightType,
                Slot2Active = isActive,
                TargetActionPrompt = t.actionPrompt,
                NegativeActionPrompts = t.negActions.ToList()
            });
        }
    }

    public static async Task<List<PR24TurnGuardPlan>> GenerateAllAuthoritativePR24PlansAsync()
    {
        var baseRequests = await GenerateAllAuthoritativeRequestsAsync();
        var pr24Plans = new List<PR24TurnGuardPlan>();

        foreach (var req in baseRequests)
        {
            var turnPlan = new PR24TurnGuardPlan
            {
                CharacterId = req.CharacterId,
                CharacterName = req.CharacterName,
                Gender = req.Gender,
                Turn = req.Turn,
                Location = req.Location,
                Action = req.Action,
                IsTransition = req.IsTransition,
                CompiledPrompt = req.CompiledPrompt,
                CompiledNegative = req.CompiledNegative,
                IdentityReferenceUrl = req.IdentityReferenceUrl,
                TargetActionPrompt = req.TargetActionPrompt,
                NegativeActionPrompts = req.NegativeActionPrompts
            };

            var charBytes = System.Text.Encoding.UTF8.GetBytes(req.CharacterId.PadRight(8, '0'));
            var deterministicTurnId = new Guid(req.Turn, (short)req.CharacterId.Length, 0, charBytes[0], charBytes[1], charBytes[2], charBytes[3], charBytes[4], charBytes[5], charBytes[6], charBytes[7]);

            var dummySnapshot = new VisualSnapshot(
                TurnId: deterministicTurnId,
                SessionId: Guid.Empty,
                CharacterId: Guid.Empty,
                SceneRevision: req.Turn,
                VisualIdentity: null,
                SceneState: new SessionSceneState(req.Location, req.Action),
                TransientState: null,
                GenerationProfile: GenerationProfile.CreateDefault(
                    seed: req.Seed,
                    parametersJson: JsonSerializer.Serialize(new
                    {
                        ipAdapter = new { weight = 0.60, endAt = 0.85 },
                        sceneContinuity = new { weight = req.Slot2Weight, endAt = req.Slot2EndAt, weightType = req.WeightType }
                    })
                )
            );

            // Attempt 1 (Standard)
            var (p1, s1) = IdentityMitigationProfileResolver.ResolveMitigation(dummySnapshot, QualityMitigationAction.Pass, 1, req.Seed);
            var fp1 = DeterministicSeedDerivation.ComputeFingerprint(
                Guid.Empty, dummySnapshot.TurnId, req.Turn, 1, s1, p1.ParametersJson ?? string.Empty,
                "VisualIdentity", 1, req.CompiledPrompt, req.CompiledNegative, null);
            turnPlan.Attempts.Add(new PR24AttemptPlan
            {
                AttemptNumber = 1,
                Seed = s1,
                Slot1Weight = 0.60,
                Slot1EndAt = 0.85,
                Slot2Weight = req.Slot2Weight,
                Slot2EndAt = req.Slot2EndAt,
                WeightType = req.WeightType,
                MitigationAction = "Pass",
                Fingerprint = fp1
            });

            // Attempt 2 (Attenuated)
            var (p2, s2) = IdentityMitigationProfileResolver.ResolveMitigation(dummySnapshot, QualityMitigationAction.RetryAttenuated, 2, req.Seed);
            var fp2 = DeterministicSeedDerivation.ComputeFingerprint(
                Guid.Empty, dummySnapshot.TurnId, req.Turn, 2, s2, p2.ParametersJson ?? string.Empty,
                "VisualIdentity", 1, req.CompiledPrompt, req.CompiledNegative, null);
            turnPlan.Attempts.Add(new PR24AttemptPlan
            {
                AttemptNumber = 2,
                Seed = s2,
                Slot1Weight = 0.65,
                Slot1EndAt = 0.85,
                Slot2Weight = 0.06,
                Slot2EndAt = 0.15,
                WeightType = "style transfer",
                MitigationAction = "RetryAttenuated",
                Fingerprint = fp2
            });

            // Attempt 3 (Isolated)
            var (p3, s3) = IdentityMitigationProfileResolver.ResolveMitigation(dummySnapshot, QualityMitigationAction.RetryIsolated, 3, req.Seed);
            var fp3 = DeterministicSeedDerivation.ComputeFingerprint(
                Guid.Empty, dummySnapshot.TurnId, req.Turn, 3, s3, p3.ParametersJson ?? string.Empty,
                "VisualIdentity", 1, req.CompiledPrompt, req.CompiledNegative, null);
            turnPlan.Attempts.Add(new PR24AttemptPlan
            {
                AttemptNumber = 3,
                Seed = s3,
                Slot1Weight = 0.70,
                Slot1EndAt = 0.85,
                Slot2Weight = 0.0,
                Slot2EndAt = 0.0,
                WeightType = "style transfer",
                MitigationAction = "RetryIsolated",
                Fingerprint = fp3
            });

            pr24Plans.Add(turnPlan);
        }

        return pr24Plans;
    }

    [Fact]
    public async Task ExportAuthoritativePR24PlanToJson()
    {
        var pr24Plans = await GenerateAllAuthoritativePR24PlansAsync();

        var artifactsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "eval_artifacts_pr24"));
        Directory.CreateDirectory(artifactsDir);
        var targetFile = Path.Combine(artifactsDir, "authoritative_pr24_plan.json");

        var json = JsonSerializer.Serialize(pr24Plans, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(targetFile, json);

        Assert.True(File.Exists(targetFile));
        Assert.Equal(24, pr24Plans.Count);
    }

    [Fact]
    public async Task Assert_Committed_Authoritative_PR24_Plan_JSON_Is_Not_Stale()
    {
        var freshlyGenerated = await GenerateAllAuthoritativePR24PlansAsync();

        var artifactsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "eval_artifacts_pr24"));
        var jsonPath = Path.Combine(artifactsDir, "authoritative_pr24_plan.json");

        Assert.True(File.Exists(jsonPath), $"Committed PR24 JSON not found at: {jsonPath}");

        var committedJson = await File.ReadAllTextAsync(jsonPath);
        var committedList = JsonSerializer.Deserialize<List<PR24TurnGuardPlan>>(committedJson);

        Assert.NotNull(committedList);
        Assert.Equal(freshlyGenerated.Count, committedList.Count);

        for (int i = 0; i < freshlyGenerated.Count; i++)
        {
            var fresh = freshlyGenerated[i];
            var comm = committedList[i];

            Assert.Equal(fresh.CharacterId, comm.CharacterId);
            Assert.Equal(fresh.Turn, comm.Turn);
            Assert.Equal(fresh.CompiledPrompt, comm.CompiledPrompt);
            Assert.Equal(fresh.CompiledNegative, comm.CompiledNegative);
            Assert.Equal(fresh.IdentityReferenceUrl, comm.IdentityReferenceUrl);
            Assert.Equal(fresh.Attempts.Count, comm.Attempts.Count);

            for (int a = 0; a < fresh.Attempts.Count; a++)
            {
                var freshAtt = fresh.Attempts[a];
                var commAtt = comm.Attempts[a];

                Assert.Equal(freshAtt.AttemptNumber, commAtt.AttemptNumber);
                Assert.Equal(freshAtt.Seed, commAtt.Seed);
                Assert.Equal(freshAtt.Slot1Weight, commAtt.Slot1Weight, precision: 4);
                Assert.Equal(freshAtt.Slot1EndAt, commAtt.Slot1EndAt, precision: 4);
                Assert.Equal(freshAtt.Slot2Weight, commAtt.Slot2Weight, precision: 4);
                Assert.Equal(freshAtt.Slot2EndAt, commAtt.Slot2EndAt, precision: 4);
                Assert.Equal(freshAtt.WeightType, commAtt.WeightType);
                Assert.Equal(freshAtt.MitigationAction, commAtt.MitigationAction);
                Assert.Equal(freshAtt.Fingerprint, commAtt.Fingerprint);
            }
        }
    }

    public sealed class PR24AttemptPlan
    {
        public int AttemptNumber { get; set; }
        public long Seed { get; set; }
        public double Slot1Weight { get; set; }
        public double Slot1EndAt { get; set; }
        public double Slot2Weight { get; set; }
        public double Slot2EndAt { get; set; }
        public string WeightType { get; set; } = "style transfer";
        public string MitigationAction { get; set; } = "Pass";
        public string Fingerprint { get; set; } = string.Empty;
    }

    public sealed class PR24TurnGuardPlan
    {
        public string CharacterId { get; set; } = string.Empty;
        public string CharacterName { get; set; } = string.Empty;
        public string Gender { get; set; } = string.Empty;
        public int Turn { get; set; }
        public string Location { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public bool IsTransition { get; set; }
        public string CompiledPrompt { get; set; } = string.Empty;
        public string CompiledNegative { get; set; } = string.Empty;
        public string IdentityReferenceUrl { get; set; } = string.Empty;
        public string TargetActionPrompt { get; set; } = string.Empty;
        public List<string> NegativeActionPrompts { get; set; } = new();
        public List<PR24AttemptPlan> Attempts { get; set; } = new();
    }

    private sealed class DummySceneStateTracker : ISceneStateTrackerService
    {
        public SceneStateDelta NextDelta { get; set; } = new();

        public Task<SessionSceneState> TrackAndExtractStateAsync(
            Character character,
            SessionSceneState? currentState,
            string userMessage,
            string assistantReply,
            CancellationToken ct = default)
        {
            var current = currentState ?? new SessionSceneState("Armory", "Center", "Armor", "Day", null, "Quiet", 0, DateTime.UtcNow);
            return Task.FromResult(current.ApplyDelta(NextDelta));
        }

        public Task<SceneStateDelta> TrackAndExtractDeltaAsync(
            Character character,
            SessionSceneState? currentState,
            string userMessage,
            string assistantReply,
            CancellationToken ct = default)
        {
            return Task.FromResult(NextDelta);
        }
    }
}
