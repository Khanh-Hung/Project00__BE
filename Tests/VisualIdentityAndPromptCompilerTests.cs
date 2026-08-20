using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Xunit;

namespace Project.Tests;

public class VisualIdentityAndPromptCompilerTests
{
    [Fact]
    public void VisualPromptCompiler_Preserves_Core_Visual_Traits_Across_Different_Scenes()
    {
        var charId = Guid.NewGuid();
        var visualIdentity = new CharacterVisualIdentity(
            Face: "delicate youthful features",
            Hair: "long flowing silver-white hair with braided ribbons",
            Eyes: "luminescent amethyst purple eyes",
            Skin: "fair porcelain skin",
            Body: "petite slender build",
            AgeAppearance: "early 20s appearance",
            ClothingStyle: "starry gothic witch robes with silver embroidery",
            Accessories: "silver crescent moon hairpin, crystal pendant",
            VisualTraits: "faint starlight aura around fingertips",
            CanonicalReferenceUrl: "https://example.com/luna_canonical.jpg"
        );

        var character = new Character("Luna", "Mage", "https://example.com/avatar.jpg", "Friendly", "Hello", "Fantasy", visualIdentity: visualIdentity)
        {
            Id = charId
        };

        var compiler = new VisualPromptCompiler();

        // 1. Compile Avatar Portrait Prompt
        var avatarPrompt = compiler.CompileAvatarPrompt(character);
        Assert.Contains("long flowing silver-white hair with braided ribbons", avatarPrompt);
        Assert.Contains("luminescent amethyst purple eyes", avatarPrompt);
        Assert.Contains("silver crescent moon hairpin, crystal pendant", avatarPrompt);

        // 2. Scene 1: Cozy Cafe (Morning) with dynamic outfit
        var sceneCafe = new SceneContext(
            Location: "cozy vintage coffee shop with wooden interior",
            TimeOfDay: "morning sunlight streaming through window",
            Outfit: "oversized soft white knitted sweater",
            Pose: "sitting across table leaning forward on elbows",
            Expression: "shy flustered smile, rosy cheeks",
            Action: "holding warm porcelain mug with both hands"
        );
        var promptCafe = compiler.CompileScenePrompt(character, sceneCafe);

        // Core visual traits MUST be preserved
        Assert.Contains("long flowing silver-white hair with braided ribbons", promptCafe);
        Assert.Contains("luminescent amethyst purple eyes", promptCafe);
        Assert.Contains("silver crescent moon hairpin, crystal pendant", promptCafe);
        // Dynamic scene elements MUST be present
        Assert.Contains("wearing oversized soft white knitted sweater", promptCafe);
        Assert.Contains("in cozy vintage coffee shop with wooden interior", promptCafe);
        Assert.Contains("shy flustered smile, rosy cheeks", promptCafe);

        // 3. Scene 2: Rainy Forest Combat (Night) without custom outfit (falls back to signature gothic witch robes)
        var sceneForest = new SceneContext(
            Location: "mystical ancient forest under torrential rain",
            TimeOfDay: "dark midnight, illuminated by glowing blue crystals",
            Outfit: null, // should fall back to signature clothing style
            Pose: "standing defensively, casting posture",
            Expression: "fierce determined glare, windblown hair",
            Action: "summoning frost shield with glowing staff"
        );
        var promptForest = compiler.CompileScenePrompt(character, sceneForest);

        // Core visual traits & default clothing style preserved
        Assert.Contains("long flowing silver-white hair with braided ribbons", promptForest);
        Assert.Contains("luminescent amethyst purple eyes", promptForest);
        Assert.Contains("starry gothic witch robes with silver embroidery", promptForest);
        // Dynamic scene elements present
        Assert.Contains("in mystical ancient forest under torrential rain", promptForest);
        Assert.Contains("fierce determined glare, windblown hair", promptForest);
    }

    [Fact]
    public void VisualPromptCompiler_Maps_Mood_And_Intensity_To_Visual_Expressions()
    {
        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var character = new Character("Luna", "Mage", "https://example.com/avatar.jpg", "Friendly", "Hello", "Fantasy") { Id = charId };

        var relationshipEmbarrassed = CharacterRelationship.Create(userId, charId, 30, CharacterMood.Embarrassed);
        relationshipEmbarrassed.UpdateMood(CharacterMood.Embarrassed, 85);

        var compiler = new VisualPromptCompiler();
        var emptyScene = new SceneContext(Location: "library");

        var promptEmbarrassed = compiler.CompileScenePrompt(character, emptyScene, relationshipEmbarrassed);
        Assert.Contains("heavy blush", promptEmbarrassed);
        Assert.Contains("shyly looking away", promptEmbarrassed);
        Assert.Contains("strong emotional expression", promptEmbarrassed);

        var relationshipAffectionate = CharacterRelationship.Create(userId, charId, 80, CharacterMood.Affectionate);
        relationshipAffectionate.UpdateMood(CharacterMood.Affectionate, 90);

        var promptAffectionate = compiler.CompileScenePrompt(character, emptyScene, relationshipAffectionate);
        Assert.Contains("deep affectionate gaze", promptAffectionate);
        Assert.Contains("loving eye contact, intimate atmosphere, subtle blush", promptAffectionate);
    }

    [Fact]
    public void Character_VisualIdentity_Setter_And_UpdateDetails_Work()
    {
        var character = new Character("Luna", "Mage", "https://example.com/avatar.jpg", "Friendly", "Hello", "Fantasy");
        Assert.Null(character.VisualIdentity);

        var identity = new CharacterVisualIdentity(
            Hair: "Golden blonde twintails",
            Eyes: "Emerald green eyes",
            ClothingStyle: "Victorian maid outfit"
        );

        character.SetVisualIdentity(identity);
        Assert.NotNull(character.VisualIdentity);
        Assert.Equal("Golden blonde twintails", character.VisualIdentity.Hair);
        Assert.Equal("Emerald green eyes", character.VisualIdentity.Eyes);

        // Update via UpdateDetails
        var updatedIdentity = new CharacterVisualIdentity(
            Hair: "Short raven black bob",
            Eyes: "Ruby red eyes",
            ClothingStyle: "Cyberpunk leather jacket"
        );

        character.UpdateDetails(
            "Luna",
            "Mage",
            "https://example.com/avatar.jpg",
            "Friendly",
            "Hello",
            "Fantasy",
            new List<string>(),
            visualIdentity: updatedIdentity,
            updateVisualIdentity: true
        );

        Assert.Equal("Short raven black bob", character.VisualIdentity.Hair);
        Assert.Equal("Ruby red eyes", character.VisualIdentity.Eyes);
    }
}
