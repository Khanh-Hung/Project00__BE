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

public class CharacterWorldSettingAndGenerationTests
{
    [Fact]
    public async Task CreateCharacterHandler_Persists_WorldSetting_And_Auto_Inserts_InitialLorebookEntries()
    {
        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        await using var context = new CoreDbContext(options);
        var uow = new UnitOfWork(context);
        var handler = new CreateCharacterHandler(uow);

        var request = new CreateCharacterRequest(
            Name: "Linh Nhi",
            Title: "Tiểu Sư Muội Nghịch Ngợm",
            AvatarUrl: "https://example.com/avatar.png",
            PersonalityPrompt: "Linh Nhi là tiểu sư muội hoạt bát của phái Thanh Vân.",
            Greeting: "*vẫy tay* Huynh đến rồi!",
            Category: "Fantasy",
            WorldName: "Cửu Châu Đại Lục",
            WorldDescription: "Thế giới tu chân thịnh vượng với 9 đại châu và hàng ngàn tông môn.",
            InitialLorebookEntries: new List<GeneratedLorebookDto>
            {
                new GeneratedLorebookDto(
                    Title: "Thanh Vân Tông",
                    Content: "Tông môn danh môn chính đạo ngự trị trên đỉnh Thanh Vân Phong.",
                    Keywords: new List<string> { "Thanh Vân", "Tông môn", "chính đạo" },
                    Category: LorebookCategory.Faction,
                    Priority: 100
                ),
                new GeneratedLorebookDto(
                    Title: "Linh Tuyền Cấm Địa",
                    Content: "Hồ nước thiêng chứa linh khí thuần khiết, nơi Linh Nhi hay trốn đến tập luyện.",
                    Keywords: new List<string> { "Linh Tuyền", "Cấm địa" },
                    Category: LorebookCategory.Location,
                    Priority: 90
                )
            }
        );

        var result = await handler.Handle(new CreateCharacterCommand(request), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("Cửu Châu Đại Lục", result.Value.WorldName);
        Assert.Equal("Thế giới tu chân thịnh vượng với 9 đại châu và hàng ngàn tông môn.", result.Value.WorldDescription);

        // Verify Character saved in DB
        var savedChar = await context.Characters.FirstOrDefaultAsync(c => c.Id == result.Value.Id);
        Assert.NotNull(savedChar);
        Assert.Equal("Cửu Châu Đại Lục", savedChar.WorldName);
        Assert.Equal("Thế giới tu chân thịnh vượng với 9 đại châu và hàng ngàn tông môn.", savedChar.WorldDescription);

        // Verify Auto Lorebook entries saved in DB linked to CharacterId
        var loreEntries = await context.LorebookEntries.Where(l => l.CharacterId == result.Value.Id).ToListAsync();
        Assert.Equal(2, loreEntries.Count);
        Assert.Contains(loreEntries, l => l.Title == "Thanh Vân Tông" && l.Category == LorebookCategory.Faction);
        Assert.Contains(loreEntries, l => l.Title == "Linh Tuyền Cấm Địa" && l.Category == LorebookCategory.Location);
    }

    [Fact]
    public void PromptCompiler_Includes_Layer_1_5_WorldSetting_When_Present()
    {
        var character = new Character(
            name: "Dạ Nguyệt",
            title: "Ma Tôn Tái Thế",
            avatarUrl: "https://example.com/avatar.png",
            personalityPrompt: "Dạ Nguyệt là ma tôn lạnh lùng.",
            greeting: "Chào kẻ phàm trần.",
            category: "Fantasy",
            worldName: "Vạn Ma Uyên Cảnh",
            worldDescription: "Vùng đất bóng tối ngập tràn chướng khí và yêu thú cổ đại."
        );

        var dummySession = new ChatSession(character.Id, Guid.NewGuid(), "Session");
        var context = new RoleplayContext(
            Character: character,
            Relationship: null,
            Memories: new List<CharacterMemory>(),
            RecentMessages: new List<ChatMessage>(),
            UserMessage: "Ngươi là ai?",
            Session: dummySession
        );

        var compiler = new PromptCompiler();
        var systemPrompt = compiler.CompileSystemPrompt(context);

        Assert.Contains("[LAYER 1.5: WORLD SETTING & UNIVERSE BACKGROUND]", systemPrompt);
        Assert.Contains("Vạn Ma Uyên Cảnh", systemPrompt);
        Assert.Contains("Vùng đất bóng tối ngập tràn chướng khí", systemPrompt);
    }
}
