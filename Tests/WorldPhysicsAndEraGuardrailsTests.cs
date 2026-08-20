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
    [InlineData(WorldGenre.CyberpunkSciFi, "Cyberware", "Zero Mystical Magic")]
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

        // 2. Mundane Slice-Of-Life Character
        var modernChar = new Character(
            name: "Minh Thư",
            title: "Nữ Sinh Cùng Bàn",
            avatarUrl: "https://example.com/avatar.png",
            personalityPrompt: "Minh Thư là cô bạn cùng bàn dễ thương.",
            greeting: "Chào cậu!",
            category: "Companion",
            worldName: "Hà Nội 2026",
            worldDescription: "Lớp học cấp 3 thời hiện đại.",
            worldGenre: WorldGenre.MundaneSliceOfLife
        );

        var modernContext = new RoleplayContext(
            Character: modernChar,
            Relationship: null,
            Memories: new List<CharacterMemory>(),
            RecentMessages: new List<ChatMessage>(),
            UserMessage: "Tớ vừa bắn chưởng Kamehameha đấy!",
            Session: new ChatSession(modernChar.Id, Guid.NewGuid(), "Session")
        );

        var modernPrompt = compiler.CompileSystemPrompt(modernContext);

        Assert.Contains("Universe Reality & Genre: MundaneSliceOfLife", modernPrompt);
        Assert.Contains("[WORLD REALITY & PHYSICS GUARDRAIL: MUNDANE SLICE-OF-LIFE REALISM]", modernPrompt);
        Assert.Contains("Zero Supernatural / Magic", modernPrompt);
        Assert.Contains("Reaction to Impossible / Fantasy User Actions", modernPrompt);
    }

    [Fact]
    public async Task CreateCharacterHandler_Persists_WorldGenre()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        await using var context = new ProjectDbContext(options);
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
            WorldGenre: WorldGenre.CyberpunkSciFi
        );

        var result = await handler.Handle(new CreateCharacterCommand(request), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(WorldGenre.CyberpunkSciFi, result.Value.WorldGenre);

        var savedChar = await context.Characters.FirstOrDefaultAsync(c => c.Id == result.Value.Id);
        Assert.NotNull(savedChar);
        Assert.Equal(WorldGenre.CyberpunkSciFi, savedChar.WorldGenre);
    }
}
