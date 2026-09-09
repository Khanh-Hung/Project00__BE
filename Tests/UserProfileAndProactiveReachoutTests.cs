using Application.Abstractions.Data;
using Application.Common;
using Application.DTOs;
using Application.Features.Chat.Commands.GenerateProactiveReachout;
using Application.Features.UserProfile.Commands.UpdateUserProfile;
using Application.Features.UserProfile.Queries.GetUserProfile;
using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories.Core;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Project.Tests;

public class UserProfileAndProactiveReachoutTests
{
    [Fact]
    public void UserProfile_Create_And_Update_Maintains_Interests_And_Traits()
    {
        var userId = Guid.NewGuid();
        var profile = UserProfile.Create(
            userId: userId,
            bio: "Mê lập trình và nghe nhạc đêm.",
            interests: new List<string> { "Lập Trình", "Nhạc Lofi", "Nuôi Mèo" },
            personalityTraits: new List<string> { "Hướng nội", "Ấm áp" },
            statusMessage: "Đang nghe Lofi..."
        );

        Assert.Equal(userId, profile.UserId);
        Assert.Equal(3, profile.GetInterests().Count);
        Assert.Contains("Nuôi Mèo", profile.GetInterests());
        Assert.Equal(2, profile.GetPersonalityTraits().Count);
        Assert.Contains("Hướng nội", profile.GetPersonalityTraits());

        // Update
        profile.Update(
            bio: "Cà phê và mèo.",
            interests: new List<string> { "Cà Phê", "Nuôi Mèo" },
            personalityTraits: new List<string> { "Hài hước" },
            statusMessage: "Thảnh thơi",
            updatedAt: Clock.Now
        );

        Assert.Equal(2, profile.GetInterests().Count);
        Assert.Contains("Cà Phê", profile.GetInterests());
        Assert.Single(profile.GetPersonalityTraits());
        Assert.Contains("Hài hước", profile.GetPersonalityTraits());
    }

    [Fact]
    public async Task GetUserProfileHandler_Auto_Initializes_Default_Profile_When_Missing()
    {
        var coreOptions = new DbContextOptionsBuilder<CoreDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        await using var coreDbContext = new CoreDbContext(coreOptions);
        var unitOfWork = new UnitOfWork(coreDbContext);
        var fakeAccountService = new FakeAccountServiceClient();
        var handler = new GetUserProfileHandler(unitOfWork, fakeAccountService);

        var userId = Guid.NewGuid();
        fakeAccountService.AddUser(userId, "user123", "Người Dùng Mới", null);

        var result = await handler.Handle(new GetUserProfileQuery(userId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(userId, result.Value.UserId);
        Assert.Equal("Người Dùng Mới", result.Value.DisplayName);
        Assert.Contains("Anime", result.Value.Interests);
    }

    [Fact]
    public async Task GenerateProactiveReachoutHandler_Orchestrates_AI_And_Creates_Session()
    {
        var coreOptions = new DbContextOptionsBuilder<CoreDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var characterId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var coreDbContext = new CoreDbContext(coreOptions);

        var character = new Character(
            name: "Lâm Uyển Nhi",
            title: "Họa Sĩ Tự Do",
            avatarUrl: "https://example.com/uyen-nhi.png",
            personalityPrompt: "Một cô gái yêu hội họa, thích ngắm hoàng hôn và nuôi mèo.",
            greeting: "Chào bạn!",
            category: "Companion",
            worldGenre: WorldGenre.MundaneSliceOfLife
        ) { Id = characterId };

        var profile = UserProfile.Create(
            userId: userId,
            bio: "Thích vẽ tranh và đi dạo công viên.",
            interests: new List<string> { "Hội Họa", "Nuôi Mèo" }
        );

        await coreDbContext.Characters.AddAsync(character);
        await coreDbContext.UserProfiles.AddAsync(profile);
        await coreDbContext.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(coreDbContext);
        var fakeAccountService = new FakeAccountServiceClient();
        fakeAccountService.AddUser(userId, "quan", "Minh Quân", null);
        var fakeLLM = new FakeProactiveLLMService();
        var handler = new GenerateProactiveReachoutHandler(unitOfWork, fakeAccountService, fakeLLM);

        var request = new ProactiveReachoutRequest(characterId, userId);
        var result = await handler.Handle(new GenerateProactiveReachoutCommand(request), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("Lâm Uyển Nhi", result.Value.CharacterName);
        Assert.Contains("Chào Minh Quân nha!", result.Value.OpeningMessage);
        Assert.Equal("Cùng đam mê hội họa và ngắm hoàng hôn", result.Value.MatchReason);

        var savedSession = await coreDbContext.ChatSessions.FirstOrDefaultAsync(s => s.Id == result.Value.SessionId);
        Assert.NotNull(savedSession);
        Assert.Single(savedSession.Messages);
        Assert.Contains("Chào Minh Quân nha!", savedSession.Messages[0].Content);
    }

    private sealed class FakeAccountServiceClient : IAccountServiceClient
    {
        private readonly Dictionary<Guid, AccountUserDto> _users = new();

        public void AddUser(Guid userId, string? userName, string? displayName, string? avatarUrl)
        {
            _users[userId] = new AccountUserDto(userId, userName, displayName, avatarUrl);
        }

        public Task<AccountUserDto?> GetUserAsync(Guid userId, CancellationToken ct = default)
        {
            _users.TryGetValue(userId, out var user);
            return Task.FromResult(user);
        }

        public Task<IReadOnlyDictionary<Guid, AccountUserDto>> GetUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
        {
            var dict = userIds
                .Where(_users.ContainsKey)
                .ToDictionary(id => id, id => _users[id]);
            return Task.FromResult<IReadOnlyDictionary<Guid, AccountUserDto>>(dict);
        }
    }

    private sealed class FakeProactiveLLMService : ILLMService
    {
        public Task<RoleplayTurnResult> GenerateRoleplayTurnAsync(RoleplayContext context, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public IAsyncEnumerable<string> GenerateRoleplayTurnStreamAsync(RoleplayContext context, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<RoleplayTurnResult> GenerateRoleplayTurnAsync(Character character, IReadOnlyCollection<ChatMessage> history, string newUserMessage, CharacterRelationship? relationship = null, IReadOnlyCollection<CharacterMemory>? memories = null, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<string> GenerateRoleplayResponseAsync(Character character, IReadOnlyCollection<ChatMessage> history, string newUserMessage, CharacterRelationship? relationship = null, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<GeneratedCharacterDto> GenerateCharacterProfileAsync(string idea, string? category = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GenerateRandomIdeasAsync(int count = 4, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GenerateRoleplaySuggestionsAsync(Character character, IReadOnlyCollection<ChatMessage> history, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GenerateAvatarResponse> GenerateAvatarAsync(GenerateAvatarRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GenerateAvatarResponse> GenerateSceneImageAsync(GenerateSceneImageRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<MemoryCandidate>> ExtractMemoryCandidatesAsync(Character character, IReadOnlyCollection<ChatMessageDto> recentMessages, CancellationToken ct = default) => Task.FromResult(new List<MemoryCandidate>());

        public Task<ProactiveAiReachoutResult> GenerateProactiveReachoutAsync(Character character, UserProfile userProfile, CancellationToken ct = default) =>
            GenerateProactiveReachoutAsync(character, userProfile, null, ct);

        public Task<ProactiveAiReachoutResult> GenerateProactiveReachoutAsync(Character character, UserProfile userProfile, string? userDisplayName = null, CancellationToken ct = default)
        {
            return Task.FromResult(new ProactiveAiReachoutResult(
                OpeningMessage: $"*[curious] lướt thấy trang bạn có chung sở thích vẽ tranh* Chào {userDisplayName ?? "bạn"} nha! Cậu cũng thích vẽ tranh phong cảnh hả?",
                MatchReason: "Cùng đam mê hội họa và ngắm hoàng hôn"
            ));
        }
    }
}
