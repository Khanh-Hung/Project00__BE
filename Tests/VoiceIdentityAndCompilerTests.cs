using Application.Abstractions.Auth;
using Application.Common;
using Application.Features.Chat.Commands.GenerateMessageVoice;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Project.Tests;

public class VoiceIdentityAndCompilerTests
{
    [Fact]
    public void VoicePromptCompiler_Extracts_Clean_Dialogue_Text_Stripping_Thoughts_And_Actions()
    {
        var compiler = new VoicePromptCompiler();

        var rawReply = "💭 *(Mình không ngờ anh ấy lại nhận ra...)* *[blush] hai má đỏ bừng, bối rối nghịch vạt áo* \"C-cảm ơn bạn nhé, lần sau mình sẽ cố gắng hơn!\" *[smile] mỉm cười nhẹ*";
        var cleaned = compiler.ExtractCleanDialogueText(rawReply);

        Assert.Equal("C-cảm ơn bạn nhé, lần sau mình sẽ cố gắng hơn!", cleaned);
        Assert.DoesNotContain("💭", cleaned);
        Assert.DoesNotContain("blush", cleaned);
        Assert.DoesNotContain("smile", cleaned);
        Assert.DoesNotContain("*", cleaned);
    }

    [Fact]
    public void VoicePromptCompiler_Maps_Mood_And_Intensity_To_Voice_Expression()
    {
        var compiler = new VoicePromptCompiler();
        var voiceProfile = new CharacterVoiceProfile("vi-VN-HoaiMyNeural", "vi-VN", "Female");

        // 1. Happy Mood with High Intensity
        var contextHappy = new VoiceContext(
            Voice: voiceProfile,
            Mood: CharacterMood.Happy,
            MoodIntensity: 85,
            AffectionScore: 40,
            RelationshipStage: "Acquaintance",
            RawText: "Hôm nay tuyệt vời quá!"
        );
        var requestHappy = compiler.CompileVoiceRequest(contextHappy);
        Assert.NotNull(requestHappy.Expression);
        Assert.Equal("Happy", requestHappy.Expression.EmotionTag);
        Assert.True(requestHappy.Expression.Rate > 1.0);
        Assert.True(requestHappy.Expression.Pitch > 0);

        // 2. Sad Mood with High Intensity
        var contextSad = new VoiceContext(
            Voice: voiceProfile,
            Mood: CharacterMood.Sad,
            MoodIntensity: 80,
            AffectionScore: 40,
            RelationshipStage: "Acquaintance",
            RawText: "Mình thấy buồn lắm..."
        );
        var requestSad = compiler.CompileVoiceRequest(contextSad);
        Assert.NotNull(requestSad.Expression);
        Assert.Equal("Sad", requestSad.Expression.EmotionTag);
        Assert.True(requestSad.Expression.Rate < 1.0);
        Assert.True(requestSad.Expression.Pitch < 0);
        Assert.Equal("Soft", requestSad.Expression.Volume);

        // 3. Affectionate Mood with High Intimacy
        var contextAffectionate = new VoiceContext(
            Voice: voiceProfile,
            Mood: CharacterMood.Affectionate,
            MoodIntensity: 90,
            AffectionScore: 85,
            RelationshipStage: "Lover",
            RawText: "Mình luôn ở bên cậu."
        );
        var requestAffectionate = compiler.CompileVoiceRequest(contextAffectionate);
        Assert.NotNull(requestAffectionate.Expression);
        Assert.Equal("Affectionate", requestAffectionate.Expression.EmotionTag);
        Assert.Equal("Whisper", requestAffectionate.Expression.Volume);
    }

    [Fact]
    public void VoicePromptCompiler_Preserves_VoiceId_Across_Emotional_Shifts()
    {
        var compiler = new VoicePromptCompiler();
        var voiceProfile = new CharacterVoiceProfile("eleven_multilingual_luna", "en-US", "Female");

        var moods = new[] { CharacterMood.Angry, CharacterMood.Excited, CharacterMood.Embarrassed, CharacterMood.Neutral };
        foreach (var mood in moods)
        {
            var context = new VoiceContext(
                Voice: voiceProfile,
                Mood: mood,
                MoodIntensity: 75,
                AffectionScore: 50,
                RelationshipStage: "Friend",
                RawText: "Hello there!"
            );
            var req = compiler.CompileVoiceRequest(context);
            Assert.Equal("eleven_multilingual_luna", req.VoiceId);
            Assert.Equal("en-US", req.Language);
        }
    }

    [Fact]
    public async Task GenerateMessageVoiceHandler_Enforces_Session_Ownership()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var charId = Guid.NewGuid();
        var legitimateUser = Guid.NewGuid();
        var attackerUser = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var character = new Character("Luna", "Mage", "https://example.com/avatar.jpg", "Friendly", "Hello", "Fantasy") { Id = charId };
        await context.Characters.AddAsync(character);

        var session = new ChatSession(charId, legitimateUser, "Legitimate Session");
        var msg = session.AddAssistantMessage("Hello User!");
        await context.ChatSessions.AddAsync(session);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var attackerProvider = new FakeCurrentUserProvider(attackerUser.ToString());
        var voiceCompiler = new VoicePromptCompiler();
        var mockVoiceService = new MockVoiceService();

        var handler = new GenerateMessageVoiceHandler(
            unitOfWork,
            attackerProvider,
            voiceCompiler,
            mockVoiceService,
            NullLogger<GenerateMessageVoiceHandler>.Instance
        );

        var command = new GenerateMessageVoiceCommand(session.Id, msg.Id);
        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    private sealed class FakeCurrentUserProvider : ICurrentUserProvider
    {
        public FakeCurrentUserProvider(string? currentUserId) => CurrentUserId = currentUserId;
        public string? CurrentUserId { get; }
        public string? CurrentUserName => "TestUser";
        public string? CurrentUserEmail => "test@example.com";
        public string? CurrentUserRole => "User";
    }

    private sealed class MockVoiceService : IVoiceGenerationService
    {
        public Task<VoiceGenerationResult> GenerateVoiceAsync(VoiceProviderRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new VoiceGenerationResult($"/uploads/audio/{Guid.NewGuid():N}.mp3", "audio/mpeg", TimeSpan.FromSeconds(3)));
        }
    }
}
