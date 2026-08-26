using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public class ImageGenerationJobConfiguration : IEntityTypeConfiguration<ImageGenerationJob>
{
    public void Configure(EntityTypeBuilder<ImageGenerationJob> builder)
    {
        builder.ToTable("ImageGenerationJobs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.SessionId).IsRequired();
        builder.Property(x => x.TurnId).IsRequired();
        builder.Property(x => x.CharacterId).IsRequired();
        builder.Property(x => x.SceneRevision).IsRequired();
        builder.Property(x => x.GenerationRequestId).IsRequired();
        builder.Property(x => x.Provider).IsRequired().HasMaxLength(64);
        builder.Property(x => x.ProviderJobId).HasMaxLength(256);
        builder.Property(x => x.Status).IsRequired();
        builder.Property(x => x.AttemptCount).IsRequired();
        builder.Property(x => x.ClaimedBy).HasMaxLength(128);
        builder.Property(x => x.LeaseUntil);
        builder.Property(x => x.Workflow).IsRequired().HasMaxLength(128);
        builder.Property(x => x.WorkflowVersion).IsRequired();
        builder.Property(x => x.AcceptedAttemptId);
        builder.Property(x => x.CurrentAttemptNumber).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.FailureReason).HasMaxLength(2048);
        builder.Property(x => x.Version).IsConcurrencyToken();

        builder.HasIndex(x => new { x.SessionId, x.GenerationRequestId }).IsUnique();
        builder.HasIndex(x => new { x.SessionId, x.TurnId, x.SceneRevision });
        builder.HasIndex(x => new { x.Status, x.LeaseUntil });
        builder.HasIndex(x => x.AcceptedAttemptId);
    }
}
