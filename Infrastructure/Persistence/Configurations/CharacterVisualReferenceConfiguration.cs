using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public class CharacterVisualReferenceConfiguration : IEntityTypeConfiguration<CharacterVisualReference>
{
    public void Configure(EntityTypeBuilder<CharacterVisualReference> builder)
    {
        builder.ToTable("CharacterVisualReferences");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CharacterId).IsRequired();
        builder.Property(x => x.ReferenceUrl).IsRequired().HasMaxLength(2048);
        builder.Property(x => x.Type).IsRequired().HasDefaultValue(VisualReferenceType.SecondaryCanonical);
        builder.Property(x => x.Status).IsRequired().HasDefaultValue(VisualReferenceStatus.Active);
        builder.Property(x => x.IsCanonical).IsRequired().HasDefaultValue(false);
        builder.Property(x => x.Priority).IsRequired().HasDefaultValue(0);

        builder.Property(x => x.VisualProfileId).IsRequired(false);
        builder.Property(x => x.ArtifactId).IsRequired(false);
        builder.Property(x => x.SourceGenerationJobId).IsRequired(false);
        builder.Property(x => x.SourceVisualRevision).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.PromotedAt).IsRequired(false);
        builder.Property(x => x.ArchivedAt).IsRequired(false);

        // Database invariant: Exactly ONE active primary canonical reference per Character
        builder.HasIndex(x => x.CharacterId)
            .HasFilter("\"IsCanonical\" = 1")
            .IsUnique();

        // Idempotency: At most one reference per (CharacterId, ArtifactId)
        builder.HasIndex(x => new { x.CharacterId, x.ArtifactId })
            .HasFilter("\"ArtifactId\" IS NOT NULL")
            .IsUnique();

        builder.HasIndex(x => new { x.CharacterId, x.Status });
        builder.HasIndex(x => new { x.CharacterId, x.Type });
        builder.HasIndex(x => x.ArtifactId);
    }
}
