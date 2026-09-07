using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class PersonalityAdaptationEvidenceConfiguration : IEntityTypeConfiguration<PersonalityAdaptationEvidence>
{
    public void Configure(EntityTypeBuilder<PersonalityAdaptationEvidence> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        builder.Property(e => e.CharacterId).IsRequired();
        builder.Property(e => e.ExecutionId).IsRequired();
        builder.Property(e => e.EvidenceType).IsRequired().HasConversion<string>().HasMaxLength(50);
        builder.Property(e => e.TraitKey).IsRequired().HasMaxLength(50);
        builder.Property(e => e.Direction).IsRequired();
        builder.Property(e => e.Strength).IsRequired();
        builder.Property(e => e.Reason).IsRequired().HasMaxLength(500);
        builder.Property(e => e.Fingerprint).IsRequired().HasMaxLength(64);
        builder.Property(e => e.IsApplied).IsRequired().HasDefaultValue(false).IsConcurrencyToken();
        builder.Property(e => e.AdaptationId).IsRequired(false);
        builder.Property(e => e.CreatedAtUtc).IsRequired();

        // Single authoritative evidence per cognitive cycle execution
        builder.HasIndex(e => new { e.CharacterId, e.ExecutionId })
               .IsUnique()
               .HasDatabaseName("IX_PersonalityAdaptationEvidences_CharacterId_ExecutionId");

        // Fast lookup for unapplied evidence aggregation
        builder.HasIndex(e => new { e.CharacterId, e.TraitKey, e.IsApplied })
               .HasDatabaseName("IX_PersonalityAdaptationEvidences_CharId_TraitKey_IsApplied");
    }
}
