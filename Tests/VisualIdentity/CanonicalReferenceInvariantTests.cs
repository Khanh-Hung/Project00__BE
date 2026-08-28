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

public sealed class CanonicalReferenceInvariantTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public CanonicalReferenceInvariantTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new CoreDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    public async Task SingleCanonicalReference_ForCharacter_IsAllowed()
    {
        await using var db = new CoreDbContext(_options);
        var charId = Guid.NewGuid();

        var canonical = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/canon1.png",
            type: VisualReferenceType.Canonical,
            status: VisualReferenceStatus.Active,
            isCanonical: true
        );

        db.CharacterVisualReferences.Add(canonical);
        await db.SaveChangesAsync();

        var count = await db.CharacterVisualReferences
            .CountAsync(r => r.CharacterId == charId && r.IsCanonical && r.Status == VisualReferenceStatus.Active);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SecondActiveCanonicalReference_ForSameCharacter_ViolatesDatabaseConstraint()
    {
        await using var db = new CoreDbContext(_options);
        var charId = Guid.NewGuid();

        var canonical1 = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/canon1.png",
            type: VisualReferenceType.Canonical,
            status: VisualReferenceStatus.Active,
            isCanonical: true
        );

        var canonical2 = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/canon2.png",
            type: VisualReferenceType.Canonical,
            status: VisualReferenceStatus.Active,
            isCanonical: true
        );

        db.CharacterVisualReferences.Add(canonical1);
        await db.SaveChangesAsync();

        db.CharacterVisualReferences.Add(canonical2);

        // Assert: Partial unique index (CharacterId WHERE IsCanonical = true AND Status = Active) throws DbUpdateException
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task ArchivedCanonicalReference_DoesNotBlockNewActiveCanonicalReference()
    {
        await using var db = new CoreDbContext(_options);
        var charId = Guid.NewGuid();

        var archivedCanonical = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/archived_canon.png",
            type: VisualReferenceType.Canonical,
            status: VisualReferenceStatus.Archived,
            isCanonical: false
        );

        db.CharacterVisualReferences.Add(archivedCanonical);
        await db.SaveChangesAsync();

        var newActiveCanonical = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/new_active_canon.png",
            type: VisualReferenceType.Canonical,
            status: VisualReferenceStatus.Active,
            isCanonical: true
        );

        db.CharacterVisualReferences.Add(newActiveCanonical);
        await db.SaveChangesAsync(); // Must succeed without constraint violation

        var count = await db.CharacterVisualReferences
            .CountAsync(r => r.CharacterId == charId && r.IsCanonical && r.Status == VisualReferenceStatus.Active);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ServicePromotion_AtomicallyDemotesPreviousCanonical_PreservingSingleCanonicalInvariant()
    {
        await using var db = new CoreDbContext(_options);
        var profileService = new CharacterVisualProfileService(db, NullLogger<CharacterVisualProfileService>.Instance);
        var referenceService = new CharacterVisualReferenceService(db, profileService, NullLogger<CharacterVisualReferenceService>.Instance);
        var charId = Guid.NewGuid();

        // 1. Initial canonical reference
        var initial = await referenceService.RegisterReferenceAsync(new RegisterVisualReferenceRequest(
            CharacterId: charId,
            ReferenceUrl: "https://cdn.project00.ai/canon_initial.png",
            IsCanonical: true,
            Type: VisualReferenceType.Canonical
        ));

        // 2. Register secondary reference
        var secondary = await referenceService.RegisterReferenceAsync(new RegisterVisualReferenceRequest(
            CharacterId: charId,
            ReferenceUrl: "https://cdn.project00.ai/canon_promoted.png",
            IsCanonical: false,
            Type: VisualReferenceType.SecondaryCanonical
        ));

        // 3. Promote secondary to canonical
        var promoted = await referenceService.PromoteToCanonicalAsync(charId, secondary.Id);

        // Verification: Exactly 1 active canonical reference exists
        var activeCanonicals = await db.CharacterVisualReferences
            .Where(r => r.CharacterId == charId && r.IsCanonical && r.Type == VisualReferenceType.Canonical && r.Status == VisualReferenceStatus.Active)
            .ToListAsync();

        Assert.Single(activeCanonicals);
        Assert.Equal(secondary.Id, activeCanonicals[0].Id);

        // Previous canonical is demoted to SecondaryCanonical
        var previous = await db.CharacterVisualReferences.FirstAsync(r => r.Id == initial.Id);
        Assert.False(previous.IsCanonical);
        Assert.Equal(VisualReferenceType.SecondaryCanonical, previous.Type);
    }
}
