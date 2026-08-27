using Domain.Entities;
using Domain.Enums;
using Xunit;

namespace Tests.VisualIdentity;

public sealed class VisualReferenceLifecycleTests
{
    [Fact]
    public void CreateReference_WithValidInputs_InitializesCorrectly()
    {
        var charId = Guid.NewGuid();
        var reference = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/ref1.png",
            type: VisualReferenceType.SecondaryCanonical,
            status: VisualReferenceStatus.Active,
            isCanonical: false,
            priority: 10
        );

        Assert.Equal(charId, reference.CharacterId);
        Assert.Equal("https://cdn.project00.ai/ref1.png", reference.ReferenceUrl);
        Assert.Equal(VisualReferenceType.SecondaryCanonical, reference.Type);
        Assert.Equal(VisualReferenceStatus.Active, reference.Status);
        Assert.False(reference.IsCanonical);
        Assert.Equal(10, reference.Priority);
        Assert.Null(reference.PromotedAt);
        Assert.Null(reference.ArchivedAt);
    }

    [Fact]
    public void PromoteToCanonical_SetsCanonicalFlagsAndPromotionTimestamp()
    {
        var charId = Guid.NewGuid();
        var reference = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/ref1.png",
            type: VisualReferenceType.SecondaryCanonical,
            status: VisualReferenceStatus.Active,
            isCanonical: false
        );

        var now = DateTime.UtcNow;
        reference.PromoteToCanonical(now);

        Assert.True(reference.IsCanonical);
        Assert.Equal(VisualReferenceType.Canonical, reference.Type);
        Assert.Equal(VisualReferenceStatus.Active, reference.Status);
        Assert.Equal(now, reference.PromotedAt);
    }

    [Fact]
    public void DemoteCanonical_ClearsCanonicalFlagAndChangesType()
    {
        var charId = Guid.NewGuid();
        var reference = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/ref1.png",
            type: VisualReferenceType.Canonical,
            status: VisualReferenceStatus.Active,
            isCanonical: true
        );

        var now = DateTime.UtcNow;
        reference.DemoteCanonical(now);

        Assert.False(reference.IsCanonical);
        Assert.Equal(VisualReferenceType.SecondaryCanonical, reference.Type);
    }

    [Fact]
    public void Archive_SetsStatusToArchivedAndClearsCanonical()
    {
        var charId = Guid.NewGuid();
        var reference = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/ref1.png",
            type: VisualReferenceType.Canonical,
            status: VisualReferenceStatus.Active,
            isCanonical: true
        );

        var now = DateTime.UtcNow;
        reference.Archive(now);

        Assert.Equal(VisualReferenceStatus.Archived, reference.Status);
        Assert.False(reference.IsCanonical);
        Assert.Equal(now, reference.ArchivedAt);
    }

    [Fact]
    public void PromoteToCanonical_WhenReferenceIsArchived_ThrowsInvalidOperationException()
    {
        var charId = Guid.NewGuid();
        var reference = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/ref1.png",
            type: VisualReferenceType.SecondaryCanonical,
            status: VisualReferenceStatus.Archived,
            isCanonical: false
        );

        var now = DateTime.UtcNow;
        var ex = Assert.Throws<InvalidOperationException>(() => reference.PromoteToCanonical(now));
        Assert.Contains("archived", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
