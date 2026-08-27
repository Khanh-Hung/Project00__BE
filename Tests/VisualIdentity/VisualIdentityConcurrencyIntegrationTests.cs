using Application.DTOs;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualIdentity;

public sealed class VisualIdentityConcurrencyIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public VisualIdentityConcurrencyIntegrationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new ProjectDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    public async Task ConcurrentPromotions_PreserveSingleCanonicalReferenceInvariant()
    {
        var charId = Guid.NewGuid();

        // Pre-seed 10 references
        var refIds = new List<Guid>();
        await using (var seedDb = new ProjectDbContext(_options))
        {
            var profileService = new CharacterVisualProfileService(seedDb, NullLogger<CharacterVisualProfileService>.Instance);
            await profileService.CreateProfileAsync(charId, eyeColor: "Red", hairColor: "Silver");

            for (int i = 0; i < 10; i++)
            {
                var r = new CharacterVisualReference(
                    characterId: charId,
                    referenceUrl: $"https://cdn.project00.ai/ref_{i}.png",
                    type: VisualReferenceType.SecondaryCanonical,
                    status: VisualReferenceStatus.Active,
                    isCanonical: false,
                    priority: i
                );
                seedDb.CharacterVisualReferences.Add(r);
                refIds.Add(r.Id);
            }
            await seedDb.SaveChangesAsync();
        }

        // 10 concurrent tasks attempting to promote their assigned reference
        var tasks = refIds.Select(async refId =>
        {
            try
            {
                await using var taskDb = new ProjectDbContext(_options);
                var profileService = new CharacterVisualProfileService(taskDb, NullLogger<CharacterVisualProfileService>.Instance);
                var referenceService = new CharacterVisualReferenceService(taskDb, profileService, NullLogger<CharacterVisualReferenceService>.Instance);

                await referenceService.PromoteToCanonicalAsync(charId, refId);
            }
            catch (Exception)
            {
                // Concurrency retries or serialization failures are expected in extreme contention
            }
        });

        await Task.WhenAll(tasks);

        // Verification: Exactly 1 active canonical reference MUST exist for the character
        await using var verifyDb = new ProjectDbContext(_options);
        var canonicals = await verifyDb.CharacterVisualReferences
            .Where(r => r.CharacterId == charId && r.IsCanonical && r.Type == VisualReferenceType.Canonical && r.Status == VisualReferenceStatus.Active)
            .ToListAsync();

        Assert.Single(canonicals);
    }
}
