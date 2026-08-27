using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public class VisualSessionStateConfiguration : IEntityTypeConfiguration<VisualSessionState>
{
    public void Configure(EntityTypeBuilder<VisualSessionState> builder)
    {
        builder.ToTable("VisualSessionStates");
        builder.HasKey(x => x.SessionId);

        builder.Property(x => x.SessionId).IsRequired();
        builder.Property(x => x.VisualRevision).IsRequired().HasDefaultValue(1);
        builder.Property(x => x.UpdatedAt).IsRequired(false);

        // Foreign key relation to SceneImages (CurrentImageId)
        builder.HasOne<SceneImage>()
            .WithMany()
            .HasForeignKey(x => x.CurrentImageId)
            .OnDelete(DeleteBehavior.SetNull);

        // Foreign key relation to ImageGenerationJobs (CurrentGenerationJobId)
        builder.HasOne<ImageGenerationJob>()
            .WithMany()
            .HasForeignKey(x => x.CurrentGenerationJobId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
