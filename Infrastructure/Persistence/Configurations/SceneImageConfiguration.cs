using Domain.Entities;
using Domain.Enums;
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
        builder.Property(x => x.GenerationRequestId).IsRequired();
        builder.Property(x => x.IsCurrent).IsRequired().HasDefaultValue(true);
        builder.Property(x => x.Workflow).IsRequired().HasMaxLength(128);
        builder.Property(x => x.WorkflowVersion).IsRequired();
        builder.Property(x => x.GenerationFingerprint).HasMaxLength(128);
        builder.Property(x => x.VisualRevision).IsRequired().HasDefaultValue(1);
        builder.Property(x => x.LifecycleStatus).IsRequired().HasDefaultValue(ArtifactLifecycleStatus.Current);

        // Predecessor reference to another SceneImage (Self-referencing FK)
        builder.HasOne<SceneImage>()
            .WithMany()
            .HasForeignKey(x => x.PredecessorArtifactId)
            .OnDelete(DeleteBehavior.SetNull);

        // Invariant: Unique per generation request attempt; non-unique per revision to support multiple regenerations
        builder.HasIndex(x => new { x.SessionId, x.GenerationRequestId }).IsUnique();
        
        // Database invariant: At most ONE active (IsCurrent = true) artifact per (SessionId, SceneRevision)
        builder.HasIndex(x => new { x.SessionId, x.SceneRevision })
            .HasFilter("\"IsCurrent\" = true")
            .IsUnique();

        builder.HasIndex(x => x.TurnId);

        builder.HasIndex(x => x.GenerationFingerprint)
            .HasFilter("\"GenerationFingerprint\" IS NOT NULL")
            .IsUnique();

        // Referential integrity to ImageGenerationJob: RESTRICT to preserve immutable historical audit trail
        builder.HasOne<ImageGenerationJob>()
            .WithMany()
            .HasForeignKey(x => x.GenerationJobId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
