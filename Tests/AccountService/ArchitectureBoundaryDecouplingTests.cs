using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.DTOs;
using Application.Features.Characters.Queries.GetPublicCharacters;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Project.Tests.AccountService;

public sealed class ArchitectureBoundaryDecouplingTests
{
    private sealed class FakeCurrentUserProvider : ICurrentUserProvider
    {
        public string? CurrentUserId => null;
    }

    private sealed class TrackingAccountServiceClient : IAccountServiceClient
    {
        public List<Guid> RequestedSingleUserIds { get; } = new();
        public List<IReadOnlyCollection<Guid>> RequestedBatchUserIds { get; } = new();
        public Dictionary<Guid, AccountUserDto> MockData { get; } = new();

        public Task<AccountUserDto?> GetUserAsync(Guid userId, CancellationToken ct = default)
        {
            RequestedSingleUserIds.Add(userId);
            MockData.TryGetValue(userId, out var user);
            return Task.FromResult(user);
        }

        public Task<IReadOnlyDictionary<Guid, AccountUserDto>> GetUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
        {
            RequestedBatchUserIds.Add(userIds);
            var result = userIds
                .Where(MockData.ContainsKey)
                .ToDictionary(id => id, id => MockData[id]);
            return Task.FromResult<IReadOnlyDictionary<Guid, AccountUserDto>>(result);
        }
    }

    [Fact]
    public void DependencyInjection_DoesNotRegister_IdentityDbContext_Or_IdentityUnitOfWork()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:CoreConnection"] = "DataSource=:memory:",
            ["Jwt:Secret"] = "test-jwt-secret-key-at-least-256-bits-long-for-hmac-sha",
            ["Jwt:Issuer"] = "AccountService",
            ["Jwt:Audience"] = "NyxorisClient",
            ["AccountService:BaseUrl"] = "http://localhost:5000"
        };

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(config);

        // Assert Identity types are NOT registered
        var descriptors = services.ToList();
        Assert.DoesNotContain(descriptors, d => d.ServiceType.Name.Contains("IdentityDbContext"));
        Assert.DoesNotContain(descriptors, d => d.ServiceType.Name.Contains("IIdentityUnitOfWork"));
        Assert.DoesNotContain(descriptors, d => d.ImplementationType?.Name.Contains("IdentityUnitOfWork") == true);
        Assert.DoesNotContain(descriptors, d => d.ServiceType.Name.Contains("IPasswordHasher"));
        Assert.DoesNotContain(descriptors, d => d.ServiceType.Name.Contains("IJwtTokenGenerator"));

        // Assert IAccountServiceClient IS registered
        Assert.Contains(descriptors, d => d.ServiceType == typeof(IAccountServiceClient));
    }

    [Fact]
    public async Task GetPublicCharactersHandler_ResolvesCreators_ViaBatchLookup_WithDistinctNonSystemIds()
    {
        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new CoreDbContext(options);

        var creator1 = Guid.NewGuid();
        var creator2 = Guid.NewGuid();

        // 4 characters: 2 share creator1, 1 has creator2, 1 has "system"
        var c1 = new Character("C1", "T1", "a1", "p1", "g1", "Anime", tags: null, isPublic: true);
        c1.SetCreated(DateTime.UtcNow, creator1.ToString());

        var c2 = new Character("C2", "T2", "a2", "p2", "g2", "Anime", tags: null, isPublic: true);
        c2.SetCreated(DateTime.UtcNow, creator1.ToString()); // duplicate creator

        var c3 = new Character("C3", "T3", "a3", "p3", "g3", "Anime", tags: null, isPublic: true);
        c3.SetCreated(DateTime.UtcNow, creator2.ToString());

        var c4 = new Character("C4", "T4", "a4", "p4", "g4", "Anime", tags: null, isPublic: true);
        c4.SetCreated(DateTime.UtcNow, "system"); // system creator

        await db.Characters.AddRangeAsync(c1, c2, c3, c4);
        await db.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(db);
        var trackingAccountService = new TrackingAccountServiceClient();
        trackingAccountService.MockData[creator1] = new AccountUserDto(creator1, "creator_one", "Creator One", "https://c1.png");
        trackingAccountService.MockData[creator2] = new AccountUserDto(creator2, "creator_two", "Creator Two", "https://c2.png");

        var handler = new GetPublicCharactersHandler(unitOfWork, trackingAccountService, new FakeCurrentUserProvider());

        var result = await handler.Handle(new GetPublicCharactersQuery(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(4, result.Value.Count);

        // Verify batch was called exactly once
        Assert.Single(trackingAccountService.RequestedBatchUserIds);
        var batchIds = trackingAccountService.RequestedBatchUserIds[0].ToList();

        // Must contain creator1 and creator2, exactly 2 distinct IDs, NO system, NO duplicates
        Assert.Equal(2, batchIds.Count);
        Assert.Contains(creator1, batchIds);
        Assert.Contains(creator2, batchIds);

        // Verify character metadata mapped correctly
        var dto1 = result.Value.First(c => c.Id == c1.Id);
        Assert.Equal("Creator One", dto1.CreatorName);
        Assert.Equal("creator_one", dto1.CreatorUserName);
        Assert.Equal("https://c1.png", dto1.CreatorAvatar);

        var dto4 = result.Value.First(c => c.Id == c4.Id);
        Assert.Equal("System", dto4.CreatorName);
    }
}
