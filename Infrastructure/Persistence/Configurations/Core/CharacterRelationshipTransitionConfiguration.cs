using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class CharacterRelationshipTransitionConfiguration : IEntityTypeConfiguration<CharacterRelationshipTransition>
{
    public void Configure(EntityTypeBuilder<CharacterRelationshipTransition> builder)
    {
        builder.ToTable("CharacterRelationshipTransitions");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.CharacterId).IsRequired();
        builder.Property(t => t.ExecutionId).IsRequired();
        builder.Property(t => t.TargetId).IsRequired();
        builder.Property(t => t.TargetType).IsRequired().HasConversion<string>().HasMaxLength(50);
        builder.Property(t => t.TransitionFingerprint).HasMaxLength(64).IsRequired();

        builder.Property(t => t.TrustDelta).IsRequired();
        builder.Property(t => t.AffectionDelta).IsRequired();
        builder.Property(t => t.FamiliarityDelta).IsRequired();

        builder.Property(t => t.OldRelationshipType).IsRequired().HasConversion<string>().HasMaxLength(50);
        builder.Property(t => t.NewRelationshipType).IsRequired().HasConversion<string>().HasMaxLength(50);

        builder.Property(t => t.VersionBefore).IsRequired();
        builder.Property(t => t.VersionAfter).IsRequired();
        builder.Property(t => t.Reason).HasMaxLength(500);
        builder.Property(t => t.AppliedAtUtc).IsRequired();

        // Idempotency uniqueness boundary: (CharacterId, ExecutionId)
        builder.HasIndex(t => new { t.CharacterId, t.ExecutionId })
               .IsUnique()
               .HasDatabaseName("IX_CharacterRelationshipTransitions_CharacterId_ExecutionId");

        builder.HasIndex(t => new { t.CharacterId, t.AppliedAtUtc })
               .HasDatabaseName("IX_CharacterRelationshipTransitions_CharacterId_AppliedAtUtc");
    }
}
