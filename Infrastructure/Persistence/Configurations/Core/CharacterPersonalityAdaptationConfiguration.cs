using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class CharacterPersonalityAdaptationConfiguration : IEntityTypeConfiguration<CharacterPersonalityAdaptation>
{
    public void Configure(EntityTypeBuilder<CharacterPersonalityAdaptation> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedOnAdd();

        builder.Property(a => a.CharacterId).IsRequired();
        builder.Property(a => a.ExecutionId).IsRequired();
        builder.Property(a => a.TraitKey).IsRequired().HasMaxLength(50);
        builder.Property(a => a.ValueBefore).IsRequired();
        builder.Property(a => a.ValueAfter).IsRequired();
        builder.Property(a => a.Delta).IsRequired();
        builder.Property(a => a.EvidenceCount).IsRequired();
        builder.Property(a => a.Fingerprint).IsRequired().HasMaxLength(64);
        builder.Property(a => a.CreatedAtUtc).IsRequired();

        builder.HasIndex(a => new { a.CharacterId, a.ExecutionId, a.TraitKey })
               .IsUnique()
               .HasDatabaseName("IX_CharacterPersonalityAdaptations_CharacterId_ExecutionId_TraitKey");

        builder.HasIndex(a => new { a.CharacterId, a.TraitKey })
               .HasDatabaseName("IX_CharacterPersonalityAdaptations_CharacterId_TraitKey");
    }
}
