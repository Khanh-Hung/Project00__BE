using Application.Common;
using Application.DTOs;
using Application.Features.Characters.Commands.CreateCharacter;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.LLM.Prompts;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Project.Tests;

public class WorldPhysicsAndEraGuardrailsTests
{
    [Theory]
    [InlineData(WorldGenre.MundaneSliceOfLife, "Zero Supernatural / Magic", "laws of physics")]
    [InlineData(WorldGenre.HighFantasy, "Power System & Magic", "Cross-World / Isekai")]
    [InlineData(WorldGenre.CyberpunkSciFi, "High-Tech Foundation", "Zero Mystical Magic")]
    [InlineData(WorldGenre.UrbanSupernatural, "URBAN SUPERNATURAL", "Masquerade")]
    [InlineData(WorldGenre.Historical, "Historical Authenticity", "Traditional Etiquette")]
    public void WorldPhysicsRuleResolver_Resolves_Accurate_Guardrails_For_All_Genres(
        WorldGenre genre,
        string expectedKeyword1,
        string expectedKeyword2)
    {
        var rules = WorldPhysicsRuleResolver.Resolve(genre);

        Assert.NotNull(rules);
        Assert.Contains(expectedKeyword1, rules);
        Assert.Contains(expectedKeyword2, rules);
    }

    [Fact]
    public void WorldPhysicsRuleResolver_Uses_CustomPhysicsRules_When_Provided()
    {
        var customChar = new Character(
            name: "Woody",
            title: "Đồ Chơi Cao Bồi",
            avatarUrl: "https://example.com/woody.png",
            personalityPrompt: "Woody là món đồ chơi sống động khi con người quay lưng đi.",
            greeting: "Có người vào phòng kìa, nằm yên!",
            category: "Companion",
            worldName: "Căn Phòng Của Andy",
            worldDescription: "Thế giới nơi đồ chơi có cảm xúc và sinh hoạt bí mật.",
            worldGenre: WorldGenre.Custom,
            customPhysicsRules: "QUY TẮC ĐỒ CHƠI: Khi có con người nhìn thấy, phải lập tức đông cứng như vật vô tri. Khi con người đi khỏi, có thể di chuyển và nói chuyện tự do."
        );

        var resolvedRules = WorldPhysicsRuleResolver.Resolve(customChar);

        Assert.Contains("[WORLD REALITY & CUSTOM PHYSICS GUARDRAIL: USER-DEFINED RULES]", resolvedRules);
        Assert.Contains("QUY TẮC ĐỒ CHƠI", resolvedRules);
        Assert.Contains("đông cứng như vật vô tri", resolvedRules);
    }

    [Fact]
    public void PromptCompiler_Renders_WorldPhysics_And_Era_Rules_Correctly()
    {
        var compiler = new PromptCompiler();

        // 1. High Fantasy Character
        var fantasyChar = new Character(
            name: "Linh Nhi",
            title: "Tiểu Sư Muội",
            avatarUrl: "https://example.com/avatar.png",
            personalityPrompt: "Linh Nhi là tiểu sư muội.",
            greeting: "Chào sư huynh!",
            category: "Fantasy",
            worldName: "Thanh Vân Tông",
            worldDescription: "Thế giới tu tiên với linh khí dồi dào.",
            worldGenre: WorldGenre.HighFantasy
        );

        var fantasyContext = new RoleplayContext(
            Character: fantasyChar,
            Relationship: null,
            Memories: new List<CharacterMemory>(),
            RecentMessages: new List<ChatMessage>(),
            UserMessage: "Huynh mang cho muội một chiếc điện thoại thông minh này!",
            Session: new ChatSession(fantasyChar.Id, Guid.NewGuid(), "Session")
        );

        var fantasyPrompt = compiler.CompileSystemPrompt(fantasyContext);

        Assert.Contains("[LAYER 1.5: WORLD SETTING & UNIVERSE BACKGROUND]", fantasyPrompt);
        Assert.Contains("Universe Reality & Genre: HighFantasy", fantasyPrompt);
        Assert.Contains("[WORLD REALITY & POWER SYSTEM GUARDRAIL: HIGH FANTASY & CULTIVATION]", fantasyPrompt);
        Assert.Contains("Mana & Stamina Constraints", fantasyPrompt);
        Assert.Contains("Cross-World / Isekai Item Reaction", fantasyPrompt);

        // 2. Custom Physics Character
        var customChar = new Character(
            name: "Vampire Lord",
            title: "Huyết Tộc",
            avatarUrl: "https://example.com/vampire.png",
            personalityPrompt: "Ma cà rồng quý tộc.",
            greeting: "Đêm nay thật đẹp.",
            category: "Fantasy",
            worldName: "Huyết Thành",
            worldDescription: "Thành phố ma cà rồng cổ xưa.",
            worldGenre: WorldGenre.Custom,
            customPhysicsRules: "Ánh sáng mặt trời trực tiếp gây bỏng nặng. Chỉ hồi phục ma lực khi uống máu nguyên chất."
        );

        var customContext = new RoleplayContext(
            Character: customChar,
            Relationship: null,
            Memories: new List<CharacterMemory>(),
            RecentMessages: new List<ChatMessage>(),
            UserMessage: "Ra ngoài phơi nắng chút không?",
            Session: new ChatSession(customChar.Id, Guid.NewGuid(), "Session")
        );

        var customPrompt = compiler.CompileSystemPrompt(customContext);

        Assert.Contains("Ánh sáng mặt trời trực tiếp gây bỏng nặng", customPrompt);
        Assert.Contains("Chỉ hồi phục ma lực khi uống máu nguyên chất", customPrompt);
    }

    [Fact]
    public async Task CreateCharacterHandler_Persists_WorldGenre_And_CustomPhysicsRules()
    {
        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        await using var context = new CoreDbContext(options);
        var uow = new UnitOfWork(context);
        var handler = new CreateCharacterHandler(uow);

        var request = new CreateCharacterRequest(
            Name: "Kaelen",
            Title: "Netrunner Solo",
            AvatarUrl: "https://example.com/avatar.png",
            PersonalityPrompt: "Kaelen là netrunner chuyên nghiệp.",
            Greeting: "*rút cyberdeck* Kết nối an toàn rồi.",
            Category: "RPG",
            WorldName: "Night City 2077",
            WorldDescription: "Đô thị tương lai ngập tràn ánh đèn neon và tội phạm công nghệ.",
            WorldGenre: WorldGenre.CyberpunkSciFi,
            CustomPhysicsRules: "Hack mạng cần băng thông não bộ tối thiểu 500TB/s."
        );

        var result = await handler.Handle(new CreateCharacterCommand(request), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(WorldGenre.CyberpunkSciFi, result.Value.WorldGenre);
        Assert.Equal("Hack mạng cần băng thông não bộ tối thiểu 500TB/s.", result.Value.CustomPhysicsRules);

        var savedChar = await context.Characters.FirstOrDefaultAsync(c => c.Id == result.Value.Id);
        Assert.NotNull(savedChar);
        Assert.Equal(WorldGenre.CyberpunkSciFi, savedChar.WorldGenre);
        Assert.Equal("Hack mạng cần băng thông não bộ tối thiểu 500TB/s.", savedChar.CustomPhysicsRules);
    }
}
