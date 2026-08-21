using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public class SceneImageConfiguration : IEntityTypeConfiguration<SceneImage>
{
    public void Configure(EntityTypeBuilder<SceneImage> builder)
    {
        builder.ToTable("SceneImages");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.SessionId).IsRequired();
        builder.Property(x => x.CharacterId).IsRequired();
        builder.Property(x => x.TurnId).IsRequired();
        builder.Property(x => x.SceneRevision).IsRequired();
        builder.Property(x => x.ImageUrl).IsRequired().HasMaxLength(2048);
        builder.Property(x => x.Prompt).IsRequired();
        builder.Property(x => x.IdentityReferenceUrl).HasMaxLength(2048);
        builder.Property(x => x.PreviousSceneImageUrl).HasMaxLength(2048);

        // Invariant: Exactly one rendered image artifact per (SessionId, SceneRevision)
        builder.HasIndex(x => new { x.SessionId, x.SceneRevision }).IsUnique();
        builder.HasIndex(x => x.TurnId);
    }
}
