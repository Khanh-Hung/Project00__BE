using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class WorldCognitiveEventConsumptionConfiguration : IEntityTypeConfiguration<WorldCognitiveEventConsumption>
{
    public void Configure(EntityTypeBuilder<WorldCognitiveEventConsumption> builder)
    {
        builder.ToTable("WorldCognitiveEventConsumptions");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.EventId).IsRequired();
        builder.Property(c => c.CharacterId).IsRequired();

        builder.Property(c => c.OccurredAtUtc).IsRequired();

        builder.Property(c => c.EventName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(c => c.Source)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(c => c.Category)
            .HasMaxLength(100);

        builder.Property(c => c.Fingerprint)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(c => c.CycleId);

        builder.Property(c => c.State)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(c => c.CreatedAtUtc).IsRequired();
        builder.Property(c => c.ConsumedAtUtc);
        builder.Property(c => c.LastAttemptAtUtc).IsRequired();
        builder.Property(c => c.AttemptCount).IsRequired().HasDefaultValue(1);

        builder.Property(c => c.FailureReason)
            .HasMaxLength(2000);

        builder.Property(c => c.Version)
            .IsConcurrencyToken()
            .IsRequired();

        // Enforce strict DB uniqueness on EventId
        builder.HasIndex(c => c.EventId)
            .IsUnique()
            .HasDatabaseName("IX_WorldCognitiveEventConsumptions_EventId");

        // Index for character-isolated queries
        builder.HasIndex(c => new { c.CharacterId, c.CreatedAtUtc })
            .HasDatabaseName("IX_WorldCognitiveEventConsumptions_CharacterId_CreatedAtUtc");
    }
}
