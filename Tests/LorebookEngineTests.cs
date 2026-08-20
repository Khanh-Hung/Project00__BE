using Application.Common;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.LLM.Prompts;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Project.Tests;

public class LorebookEngineTests
{
    [Fact]
    public async Task LorebookEngine_Matches_Dynamic_Entries_By_Keywords()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var charId = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var entry1 = new LorebookEntry(
            characterId: charId,
            title: "Hiên Viên Kiếm",
            content: "Thần kiếm thượng cổ sở hữu uy lực phong ma diệt thần.",
            keywords: new List<string> { "hiên viên kiếm", "thần kiếm" },
            category: LorebookCategory.Item,
            isConstant: false,
            priority: 150
        );

        var entry2 = new LorebookEntry(
            characterId: charId,
            title: "Vạn Ma Quật",
            content: "Vùng đất cấm địa tràn ngập ma khí và cạm bẫy.",
            keywords: new List<string> { "vạn ma quật", "cấm địa" },
            category: LorebookCategory.Location,
            isConstant: false,
            priority: 100
        );

        var entry3 = new LorebookEntry(
            characterId: charId,
            title: "Tập Đoàn Arasaka",
            content: "Tập đoàn công nghệ hắc ám trong thế giới cyberpunk.",
            keywords: new List<string> { "arasaka" },
            category: LorebookCategory.Faction,
            isConstant: false,
            priority: 50
        );

        await context.LorebookEntries.AddRangeAsync(entry1, entry2, entry3);
        await context.SaveChangesAsync();

        var uow = new UnitOfWork(context);
        var engine = new LorebookEngine(uow, NullLogger<LorebookEngine>.Instance);

        var matched = await engine.MatchLorebookEntriesAsync(
            characterId: charId,
            userMessage: "Ta muốn cùng nàng đến Cấm Địa tìm Hiên Viên Kiếm!",
            recentMessages: Array.Empty<ChatMessage>(),
            maxTokenBudget: 800
        );

        Assert.Equal(2, matched.Count);
        Assert.Contains(matched, e => e.Title == "Hiên Viên Kiếm");
        Assert.Contains(matched, e => e.Title == "Vạn Ma Quật");
        Assert.DoesNotContain(matched, e => e.Title == "Tập Đoàn Arasaka");
    }

    [Fact]
    public async Task LorebookEngine_Always_Includes_Constant_Entries()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var charId = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var constantEntry = new LorebookEntry(
            characterId: charId,
            title: "Luật Ma Giới",
            content: "Kẻ yếu bắt buộc phải phục tùng kẻ mạnh vô điều kiện.",
            keywords: new List<string>(),
            category: LorebookCategory.Rule,
            isConstant: true,
            priority: 200
        );

        await context.LorebookEntries.AddAsync(constantEntry);
        await context.SaveChangesAsync();

        var uow = new UnitOfWork(context);
        var engine = new LorebookEngine(uow, NullLogger<LorebookEngine>.Instance);

        var matched = await engine.MatchLorebookEntriesAsync(
            characterId: charId,
            userMessage: "Hôm nay trời đẹp quá.",
            recentMessages: Array.Empty<ChatMessage>(),
            maxTokenBudget: 800
        );

        Assert.Single(matched);
        Assert.Equal("Luật Ma Giới", matched[0].Title);
        Assert.True(matched[0].IsConstant);
    }

    [Fact]
    public void PromptCompiler_Renders_Lorebook_Section_Correctly()
    {
        var compiler = new PromptCompiler();
        var character = new Character("Lâm Phong", "Kiếm Tôn", "https://example.com/lp.jpg", "Lạnh lùng", "Chào", "Tu Tiên");
        var session = new ChatSession(character.Id, Guid.NewGuid(), "Test");

        var loreEntry = new LorebookEntry(
            character.Id,
            "Cửu U Kiếm Phổ",
            "Bí kíp thượng thừa gồm 9 thức tuyệt sát.",
            new List<string> { "cửu u kiếm phổ" },
            LorebookCategory.Item,
            false,
            100
        );

        var context = new RoleplayContext(
            Character: character,
            Relationship: null,
            Memories: Array.Empty<CharacterMemory>(),
            RecentMessages: Array.Empty<ChatMessage>(),
            UserMessage: "Dạy ta Cửu U Kiếm Phổ đi",
            Session: session,
            LorebookEntries: new List<LorebookEntry> { loreEntry }
        );

        var systemPrompt = compiler.CompileSystemPrompt(context);

        Assert.Contains("[LAYER 2.5: WORLD LORE & UNIVERSE RULES]", systemPrompt);
        Assert.Contains("【Item: Cửu U Kiếm Phổ】", systemPrompt);
        Assert.Contains("Bí kíp thượng thừa gồm 9 thức tuyệt sát.", systemPrompt);
    }
}
