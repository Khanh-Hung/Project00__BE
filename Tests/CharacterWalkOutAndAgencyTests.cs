using Application.Common;
using Application.DTOs;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.LLM.Prompts;
using Xunit;

namespace Project.Tests;

public class CharacterWalkOutAndAgencyTests
{
    [Fact]
    public void PromptCompiler_Renders_Intimacy_Consent_AntiGodmoding_And_WalkOut_Layer()
    {
        var compiler = new PromptCompiler();
        var character = new Character(
            name: "Tuyệt Tình Tiên Tử",
            title: "Cung Chủ",
            avatarUrl: "https://example.com/avatar.png",
            personalityPrompt: "Một cung chủ lạnh lùng, ghét kẻ đê tiện.",
            greeting: "Ngươi tìm ta có việc gì?",
            category: "Fantasy",
            worldName: "Tuyệt Tình Cốc",
            worldDescription: "Thế giới tu tiên băng giá.",
            worldGenre: WorldGenre.HighFantasy
        );

        var relationship = CharacterRelationship.Create(
            character.Id,
            Guid.NewGuid(),
            initialAffection: -50,
            initialMood: CharacterMood.Angry,
            initialMoodIntensity: 90,
            initialTimestamp: Clock.Now
        );

        var context = new RoleplayContext(
            Character: character,
            Relationship: relationship,
            Memories: new List<CharacterMemory>(),
            RecentMessages: new List<ChatMessage>(),
            UserMessage: "*ôm chầm lấy nàng và cưỡng hôn*",
            Session: new ChatSession(character.Id, Guid.NewGuid(), "Test")
        );

        var systemPrompt = compiler.CompileSystemPrompt(context);

        Assert.Contains("[LAYER 2.2: INTIMACY BOUNDARIES, CONSENT, ANTI-GODMODING & WALK-OUT AGENCY]", systemPrompt);
        Assert.Contains("CONSENT & INTIMACY BOUNDARIES (Current Affection: -50 / 100", systemPrompt);
        Assert.Contains("ABSOLUTELY PROHIBITED from accepting romantic intimacy", systemPrompt);
        Assert.Contains("ANTI-GODMODING & INDOMITABLE WILL", systemPrompt);
        Assert.Contains("THE RIGHT TO WALK OUT / TERMINATE CONVERSATION", systemPrompt);
        Assert.Contains("\"hasWalkedOut\"", systemPrompt);
        Assert.Contains("\"walkOutReason\"", systemPrompt);
    }

    [Fact]
    public void ChatSession_Transitions_To_WalkedOut_State_And_Reopens()
    {
        var session = new ChatSession(Guid.NewGuid(), Guid.NewGuid(), "Chiến Trường");
        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Null(session.WalkedOutAt);
        Assert.Null(session.WalkOutReason);

        var now = Clock.Now;
        session.WalkOut("Xúc phạm nhân phẩm và quấy rối nghiêm trọng.", now);

        Assert.Equal(SessionStatus.WalkedOut, session.Status);
        Assert.Equal("Xúc phạm nhân phẩm và quấy rối nghiêm trọng.", session.WalkOutReason);
        Assert.Equal(now, session.WalkedOutAt);

        // Reopen session
        session.Reopen();
        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Null(session.WalkOutReason);
        Assert.Null(session.WalkedOutAt);
    }
}
