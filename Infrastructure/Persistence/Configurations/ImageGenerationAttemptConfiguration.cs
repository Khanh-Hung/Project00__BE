using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public class ImageGenerationAttemptConfiguration : IEntityTypeConfiguration<ImageGenerationAttempt>
{
    public void Configure(EntityTypeBuilder<ImageGenerationAttempt> builder)
    {
        builder.ToTable("ImageGenerationAttempts");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.GenerationJobId).IsRequired();
        builder.Property(x => x.TurnId).IsRequired();
        builder.Property(x => x.SceneRevision).IsRequired();
        builder.Property(x => x.AttemptNumber).IsRequired();
        builder.Property(x => x.DerivedSeed).IsRequired();
        builder.Property(x => x.ParametersJson).IsRequired();
        builder.Property(x => x.GenerationFingerprint).IsRequired().HasMaxLength(128);
        builder.Property(x => x.Status).IsRequired().HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.ImageUrl).HasMaxLength(2048);
        builder.Property(x => x.ProviderJobId).HasMaxLength(256);
        builder.Property(x => x.ClaimedBy).HasMaxLength(128);
        builder.Property(x => x.ErrorMessage).HasMaxLength(2048);
        builder.Property(x => x.AcceptedArtifactId).IsRequired(false);

        // Strict Database-Enforced Invariant: Unique per GenerationFingerprint
        builder.HasIndex(x => x.GenerationFingerprint).IsUnique();

        builder.HasIndex(x => new { x.GenerationJobId, x.AttemptNumber });

        builder.HasOne<ImageGenerationJob>()
            .WithMany()
            .HasForeignKey(x => x.GenerationJobId)
            .OnDelete(DeleteBehavior.Cascade);

        // Authoritative Foreign Key: Direct reference to accepted SceneImage with Restrict behavior to protect audit trail
        builder.HasOne<SceneImage>()
            .WithMany()
            .HasForeignKey(x => x.AcceptedArtifactId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
