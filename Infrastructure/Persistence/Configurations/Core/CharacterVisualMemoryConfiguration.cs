using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public class CharacterVisualMemoryConfiguration : IEntityTypeConfiguration<CharacterVisualMemory>
{
    public void Configure(EntityTypeBuilder<CharacterVisualMemory> builder)
    {
        builder.ToTable("CharacterVisualMemories");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CharacterId).IsRequired();
        builder.Property(x => x.VisualProfileVersion).IsRequired().HasDefaultValue(1);
        builder.Property(x => x.SceneRevision).IsRequired().HasDefaultValue(1);
        builder.Property(x => x.ArtifactId).IsRequired();

        builder.Property(x => x.Context).HasMaxLength(2048);
        builder.Property(x => x.Tags).HasMaxLength(1024);
        builder.Property(x => x.QualityScore).IsRequired(false);
        builder.Property(x => x.IdentityScore).IsRequired(false);
        builder.Property(x => x.FeatureScore).IsRequired(false);

        builder.HasIndex(x => new { x.CharacterId, x.VisualProfileVersion });
        builder.HasIndex(x => new { x.CharacterId, x.SceneRevision });

        // Idempotency: At most one memory entry per (CharacterId, ArtifactId)
        builder.HasIndex(x => new { x.CharacterId, x.ArtifactId }).IsUnique();
        builder.HasIndex(x => x.ArtifactId);
    }
}
