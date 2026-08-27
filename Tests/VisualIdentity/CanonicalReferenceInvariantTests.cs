using Application.DTOs;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualIdentity;

public sealed class CanonicalReferenceInvariantTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public CanonicalReferenceInvariantTests()
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
    public async Task SingleCanonicalReference_ForCharacter_IsAllowed()
    {
        await using var db = new ProjectDbContext(_options);
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
            .CountAsync(r => r.CharacterId == charId && r.IsCanonical);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SecondActiveCanonicalReference_ForSameCharacter_ViolatesDatabaseConstraint()
    {
        await using var db = new ProjectDbContext(_options);
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

        // Assert: Partial unique index throws DbUpdateException on second active canonical insert
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task ServicePromotion_AtomicallyDemotesPreviousCanonical_PreservingSingleCanonicalInvariant()
    {
        await using var db = new ProjectDbContext(_options);
        var profileService = new CharacterVisualProfileService(db, NullLogger<CharacterVisualProfileService>.Instance);
        var referenceService = new CharacterVisualReferenceService(db, profileService, NullLogger<CharacterVisualReferenceService>.Instance);
        var charId = Guid.NewGuid();

        // 1. Register first canonical reference
        var ref1 = await referenceService.RegisterReferenceAsync(new RegisterVisualReferenceRequest(
            CharacterId: charId,
            ReferenceUrl: "https://cdn.project00.ai/ref1.png",
            IsCanonical: true,
            Type: VisualReferenceType.Canonical
        ));

        // 2. Register second non-canonical reference
        var ref2 = await referenceService.RegisterReferenceAsync(new RegisterVisualReferenceRequest(
            CharacterId: charId,
            ReferenceUrl: "https://cdn.project00.ai/ref2.png",
            IsCanonical: false,
            Type: VisualReferenceType.SecondaryCanonical
        ));

        // 3. Promote ref2 to canonical
        var promoted = await referenceService.PromoteToCanonicalAsync(charId, ref2.Id);
        Assert.True(promoted.IsCanonical);

        // 4. Verify in DB: exactly one active canonical exists (ref2), and ref1 was demoted
        var canonicals = await db.CharacterVisualReferences
            .Where(r => r.CharacterId == charId && r.IsCanonical && r.Type == VisualReferenceType.Canonical && r.Status == VisualReferenceStatus.Active)
            .ToListAsync();

        Assert.Single(canonicals);
        Assert.Equal(ref2.Id, canonicals[0].Id);

        var ref1InDb = await db.CharacterVisualReferences.FindAsync(ref1.Id);
        Assert.NotNull(ref1InDb);
        Assert.False(ref1InDb.IsCanonical);
        Assert.Equal(VisualReferenceType.SecondaryCanonical, ref1InDb.Type);
    }
}
