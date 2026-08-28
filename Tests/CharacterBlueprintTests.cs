using System.Text.Json;
using Domain.Entities;
using Domain.ValueObjects;
using Infrastructure.LLM.Prompts;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Project.Tests;

public class CharacterBlueprintTests
{
    private static CharacterBlueprint CreateSampleBlueprint()
    {
        return new CharacterBlueprint(
            Psychology: new PsychologyProfile(
                Desires: "Muốn bảo vệ vương quốc và người thân",
                Fears: "Sợ sự phản bội và bóng tối",
                Insecurities: "Cảm thấy bản thân chưa đủ mạnh mẽ",
                CoreBeliefs: "Công lý luôn chiến thắng",
                InternalConflicts: "Giằng xé giữa bổn phận và tình cảm cá nhân",
                Values: "Trung thực, quả cảm, trung thành"
            ),
            Behavior: new BehaviorProfile(
                WhenHappy: "Cười tươi và chủ động mời bạn uống trà",
                WhenSad: "Ngồi trầm ngâm một mình nhìn ra cửa sổ",
                WhenAngry: "Siết chặt nắm tay và nói giọng lạnh lùng",
                WhenTeased: "Đỏ mặt và quay đi giả vờ bận rộn",
                WhenPraised: "Gãi đầu ngượng ngùng khiêm tốn từ chối",
                WhenRejected: "Cúi đầu chấp nhận nhưng ánh mắt đượm buồn"
            ),
            Expression: new ExpressionProfile(
                SpeechStyle: "Trang trọng nhưng ấm áp",
                Formality: "Lịch thiệp",
                HumorStyle: "Hài hước nhẹ nhàng, không thô tục",
                EmojiUsage: "Hiếm khi dùng emoji",
                TypicalPhrases: new List<string> { "Vì danh dự!", "Tôi tin tưởng bạn.", "Đừng lo lắng." }
            ),
            Rules: new CharacterRules(
                MustDo: new List<string> { "Luôn giữ lời hứa", "Bảo vệ kẻ yếu" },
                MustNotDo: new List<string> { "Không bao giờ phản bội", "Không dùng thủ đoạn bẩn thỉu" },
                AntiSycophancy: "Giữ vững lập trường, sẵn sàng phản bác nếu người chơi đưa ra quyết định sai trái.",
                Boundaries: new List<string> { "Không bao giờ chấp nhận xúc phạm gia đình" }
            )
        );
    }

    [Fact]
    public void Character_Accepts_And_Preserves_Blueprint()
    {
        // Arrange
        var blueprint = CreateSampleBlueprint();

        // Act
        var character = new Character(
            name: "Arthur",
            title: "Kỵ sĩ ánh sáng",
            avatarUrl: "https://example.com/avatar.jpg",
            personalityPrompt: "Dũng cảm và quả cảm",
            greeting: "Chào nhà thám hiểm!",
            category: "Hiệp sĩ",
            blueprint: blueprint
        );

        // Assert
        Assert.NotNull(character.Blueprint);
        Assert.Equal("Muốn bảo vệ vương quốc và người thân", character.Blueprint.Psychology?.Desires);
        Assert.Equal("Cười tươi và chủ động mời bạn uống trà", character.Blueprint.Behavior?.WhenHappy);
        Assert.Equal("Trang trọng nhưng ấm áp", character.Blueprint.Expression?.SpeechStyle);
        Assert.Contains("Luôn giữ lời hứa", character.Blueprint.Rules?.MustDo ?? []);
    }

    [Fact]
    public void Character_Updates_And_Clears_Blueprint()
    {
        // Arrange
        var initialBlueprint = CreateSampleBlueprint();
        var character = new Character(
            name: "Arthur",
            title: "Kỵ sĩ",
            avatarUrl: "https://example.com/avatar.jpg",
            personalityPrompt: "Dũng cảm",
            greeting: "Chào!",
            category: "Hiệp sĩ",
            blueprint: initialBlueprint
        );

        // Act - Update blueprint
        var updatedBlueprint = new CharacterBlueprint(
            Psychology: new PsychologyProfile(Desires: "Tìm kiếm sự bình yên")
        );
        character.UpdateDetails(
            name: "Arthur",
            title: "Kỵ sĩ ẩn dật",
            avatarUrl: "https://example.com/avatar.jpg",
            personalityPrompt: "Trầm lặng",
            greeting: "Xin chào",
            category: "Hiệp sĩ",
            tags: new List<string>(),
            blueprint: updatedBlueprint,
            updateBlueprint: true
        );

        // Assert
        Assert.Equal("Tìm kiếm sự bình yên", character.Blueprint?.Psychology?.Desires);

        // Act - Clear blueprint
        character.SetBlueprint(null);

        // Assert
        Assert.Null(character.Blueprint);
    }

    [Fact]
    public void RoleplayPrompts_Compiles_All_Blueprint_Sections()
    {
        // Arrange
        var blueprint = CreateSampleBlueprint();
        var character = new Character(
            name: "Arthur",
            title: "Kỵ sĩ ánh sáng",
            avatarUrl: "https://example.com/avatar.jpg",
            personalityPrompt: "Dũng cảm và quả cảm",
            greeting: "Chào nhà thám hiểm!",
            category: "Hiệp sĩ",
            blueprint: blueprint
        );

        // Act
        var systemPrompt = RoleplayPrompts.BuildSystemPrompt(character);

        // Assert
        Assert.Contains("DEEP PSYCHOLOGICAL BLUEPRINT:", systemPrompt);
        Assert.Contains("Secret Desire: Muốn bảo vệ vương quốc và người thân", systemPrompt);
        Assert.Contains("BEHAVIORAL REACTION PATTERNS:", systemPrompt);
        Assert.Contains("When Happy: Cười tươi và chủ động mời bạn uống trà", systemPrompt);
        Assert.Contains("EXPRESSION & VOICE STYLE:", systemPrompt);
        Assert.Contains("Speech Style: Trang trọng nhưng ấm áp", systemPrompt);
        Assert.Contains("Typical Phrases: Vì danh dự!, Tôi tin tưởng bạn., Đừng lo lắng.", systemPrompt);
        Assert.Contains("AUTHORITATIVE CHARACTER RULES:", systemPrompt);
        Assert.Contains("Anti-Sycophancy Principle: Giữ vững lập trường", systemPrompt);
        Assert.Contains("Must Do: Luôn giữ lời hứa; Bảo vệ kẻ yếu", systemPrompt);
    }

    [Fact]
    public void Blueprint_JsonSerialization_RoundTrips_Correctly()
    {
        // Arrange
        var blueprint = CreateSampleBlueprint();

        // Act
        var json = JsonSerializer.Serialize(blueprint);
        var deserialized = JsonSerializer.Deserialize<CharacterBlueprint>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(blueprint.Psychology?.Desires, deserialized.Psychology?.Desires);
        Assert.Equal(blueprint.Behavior?.WhenAngry, deserialized.Behavior?.WhenAngry);
        Assert.Equal(blueprint.Expression?.TypicalPhrases?.Count, deserialized.Expression?.TypicalPhrases?.Count);
        Assert.Equal(blueprint.Rules?.AntiSycophancy, deserialized.Rules?.AntiSycophancy);
    }

    [Fact]
    public async Task EntityFrameworkCore_Persists_And_Loads_CharacterBlueprint()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var blueprint = CreateSampleBlueprint();
        var characterId = Guid.NewGuid();

        var character = new Character(
            name: "Lancelot",
            title: "Hiệp sĩ hồ nước",
            avatarUrl: "https://example.com/lancelot.jpg",
            personalityPrompt: "Trầm mặc và kiên định",
            greeting: "Chào ngài.",
            category: "Hiệp sĩ",
            blueprint: blueprint
        );

        // Act - Save
        await using (var context = new CoreDbContext(options))
        {
            context.Characters.Add(character);
            await context.SaveChangesAsync();
            characterId = character.Id;
        }

        // Act - Load
        await using (var context = new CoreDbContext(options))
        {
            var loaded = await context.Characters.FindAsync(characterId);

            // Assert
            Assert.NotNull(loaded);
            Assert.NotNull(loaded.Blueprint);
            Assert.Equal("Muốn bảo vệ vương quốc và người thân", loaded.Blueprint.Psychology?.Desires);
            Assert.Equal("Cười tươi và chủ động mời bạn uống trà", loaded.Blueprint.Behavior?.WhenHappy);
            Assert.Equal("Trang trọng nhưng ấm áp", loaded.Blueprint.Expression?.SpeechStyle);
            Assert.Contains("Luôn giữ lời hứa", loaded.Blueprint.Rules?.MustDo ?? []);
        }
    }
}
