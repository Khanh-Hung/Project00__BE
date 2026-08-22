using Application.Abstractions.Auth;
using Application.DTOs;
using Application.Features.Characters.Commands.DeleteCharacter;
using Application.Features.Characters.Commands.UpdateCharacter;
using Application.Features.Chat.Commands.DeleteChatSession;
using Application.Features.Chat.Commands.RollbackChatMessage;
using Application.Features.Chat.Queries.GetChatSession;
using Application.Features.Chat.Queries.GetRoleplaySuggestions;
using Application.Features.Lorebook.Commands.CreateLorebookEntry;
using Application.Features.Lorebook.Commands.DeleteLorebookEntry;
using Application.Features.UserProfile.Commands.UpdateUserProfile;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Project.Tests;

public class SecurityAuthorizationTests
{
    private sealed class TestCurrentUserProvider : ICurrentUserProvider
    {
        public string? CurrentUserId { get; set; }
        public TestCurrentUserProvider(string? userId) => CurrentUserId = userId;
    }

    [Fact]
    public async Task GetChatSession_Rejects_DifferentUser()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var charId = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var character = new Character("Alice", "Mage", "", "Friendly", "Hi", "Fantasy") { Id = charId };
        var sessionA = new ChatSession(charId, userA, "User A Session");
        await context.Characters.AddAsync(character);
        await context.ChatSessions.AddAsync(sessionA);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var userBProvider = new TestCurrentUserProvider(userB.ToString());
        var handler = new GetChatSessionHandler(unitOfWork, userBProvider);

        var result = await handler.Handle(new GetChatSessionQuery(sessionA.Id), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task DeleteChatSession_Rejects_DifferentUser()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var charId = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var character = new Character("Alice", "Mage", "", "Friendly", "Hi", "Fantasy") { Id = charId };
        var sessionA = new ChatSession(charId, userA, "User A Session");
        await context.Characters.AddAsync(character);
        await context.ChatSessions.AddAsync(sessionA);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var userBProvider = new TestCurrentUserProvider(userB.ToString());
        var handler = new DeleteChatSessionHandler(unitOfWork, userBProvider);

        var result = await handler.Handle(new DeleteChatSessionCommand(sessionA.Id), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task RollbackChatMessage_Rejects_DifferentUser()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var charId = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var character = new Character("Alice", "Mage", "", "Friendly", "Hi", "Fantasy") { Id = charId };
        var sessionA = new ChatSession(charId, userA, "User A Session");
        var msg = sessionA.AddUserMessage("Hello");
        await context.Characters.AddAsync(character);
        await context.ChatSessions.AddAsync(sessionA);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var userBProvider = new TestCurrentUserProvider(userB.ToString());
        var handler = new RollbackChatMessageHandler(unitOfWork, userBProvider);

        var result = await handler.Handle(new RollbackChatMessageCommand(sessionA.Id, msg.Id), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task UpdateUserProfile_Rejects_DifferentUser()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var profileA = Domain.Entities.UserProfile.Create(userA, "User A", null, "Bio A", null, null, null);
        await context.UserProfiles.AddAsync(profileA);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var userBProvider = new TestCurrentUserProvider(userB.ToString());
        var handler = new UpdateUserProfileHandler(unitOfWork, userBProvider);

        var request = new UpdateUserProfileRequest("Hacked Display Name", null, "Hacked Bio", null, null, null);
        var result = await handler.Handle(new UpdateUserProfileCommand(userA, request), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task UpdateCharacter_Rejects_DifferentCreator()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var character = new Character("Alice", "Mage", "", "Friendly", "Hi", "Fantasy");
        character.SetCreated(DateTime.UtcNow, userA.ToString());
        await context.Characters.AddAsync(character);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var userBProvider = new TestCurrentUserProvider(userB.ToString());
        var handler = new UpdateCharacterHandler(unitOfWork, userBProvider);

        var updateReq = new UpdateCharacterRequest("Modified Alice", "Mage", "", "Hostile", "Bye", "Fantasy", null, true, 0, null, null, null, null, null, null, null, WorldGenre.MundaneSliceOfLife, null);
        var result = await handler.Handle(new UpdateCharacterCommand(character.Id, updateReq), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task DeleteCharacter_Rejects_DifferentCreator()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var character = new Character("Alice", "Mage", "", "Friendly", "Hi", "Fantasy");
        character.SetCreated(DateTime.UtcNow, userA.ToString());
        await context.Characters.AddAsync(character);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var userBProvider = new TestCurrentUserProvider(userB.ToString());
        var handler = new DeleteCharacterHandler(unitOfWork, userBProvider);

        var result = await handler.Handle(new DeleteCharacterCommand(character.Id), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task CreateLorebookEntry_Rejects_DifferentCharacterCreator()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var character = new Character("Alice", "Mage", "", "Friendly", "Hi", "Fantasy");
        character.SetCreated(DateTime.UtcNow, userA.ToString());
        await context.Characters.AddAsync(character);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var userBProvider = new TestCurrentUserProvider(userB.ToString());
        var handler = new CreateLorebookEntryHandler(unitOfWork, userBProvider);

        var req = new CreateLorebookEntryRequest(character.Id, "Secret Entry", "Injected secret content", new List<string> { "secret" }, LorebookCategory.Faction, false, 100);
        var result = await handler.Handle(new CreateLorebookEntryCommand(req), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task DeleteLorebookEntry_Rejects_DifferentCharacterCreator()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var character = new Character("Alice", "Mage", "", "Friendly", "Hi", "Fantasy");
        character.SetCreated(DateTime.UtcNow, userA.ToString());
        await context.Characters.AddAsync(character);

        var entry = new LorebookEntry(character.Id, "Legit Entry", "Content", null, LorebookCategory.Faction, false, 50);
        await context.LorebookEntries.AddAsync(entry);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var userBProvider = new TestCurrentUserProvider(userB.ToString());
        var handler = new DeleteLorebookEntryHandler(unitOfWork, userBProvider);

        var result = await handler.Handle(new DeleteLorebookEntryCommand(entry.Id), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task GetCharacterById_Hides_PrivateCharacter_From_OtherUsers()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var character = new Character("Alice", "Mage", "", "Friendly", "Hi", "Fantasy", isPublic: false);
        character.SetCreated(DateTime.UtcNow, userA.ToString());
        await context.Characters.AddAsync(character);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var userBProvider = new TestCurrentUserProvider(userB.ToString());
        var handler = new Application.Features.Characters.Queries.GetCharacterById.GetCharacterByIdHandler(unitOfWork, userBProvider);

        var result = await handler.Handle(new Application.Features.Characters.Queries.GetCharacterById.GetCharacterByIdQuery(character.Id), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
    }

    [Fact]
    public async Task GetCharacterLorebook_Hides_PrivateCharacter_From_OtherUsers()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var character = new Character("Alice", "Mage", "", "Friendly", "Hi", "Fantasy", isPublic: false);
        character.SetCreated(DateTime.UtcNow, userA.ToString());
        await context.Characters.AddAsync(character);

        var entry = new LorebookEntry(character.Id, "Secret Lore", "Top secret", null, LorebookCategory.Location, false, 10);
        await context.LorebookEntries.AddAsync(entry);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var userBProvider = new TestCurrentUserProvider(userB.ToString());
        var handler = new Application.Features.Lorebook.Queries.GetCharacterLorebook.GetCharacterLorebookHandler(unitOfWork, userBProvider);

        var result = await handler.Handle(new Application.Features.Lorebook.Queries.GetCharacterLorebook.GetCharacterLorebookQuery(character.Id), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
    }
}
