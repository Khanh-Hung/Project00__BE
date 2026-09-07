using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class CharacterOutboxMessageConfiguration : IEntityTypeConfiguration<CharacterOutboxMessage>
{
    public void Configure(EntityTypeBuilder<CharacterOutboxMessage> builder)
    {
        builder.ToTable("CharacterOutboxMessages");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.EventId).IsRequired();
        builder.Property(m => m.CharacterId).IsRequired();

        builder.Property(m => m.EventType)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(m => m.PayloadJson)
            .IsRequired()
            .HasColumnType("jsonb");

        builder.Property(m => m.Fingerprint)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(m => m.Status)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(m => m.OccurredAtUtc).IsRequired();
        builder.Property(m => m.CreatedAtUtc).IsRequired();

        builder.Property(m => m.AttemptCount)
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(m => m.MaxRetries)
            .IsRequired()
            .HasDefaultValue(3);

        builder.Property(m => m.ProcessedAtUtc);
        builder.Property(m => m.LastError).HasMaxLength(2000);

        builder.Property(m => m.Version)
            .IsConcurrencyToken()
            .IsRequired();

        // Enforce strict DB uniqueness on EventId
        builder.HasIndex(m => m.EventId)
            .IsUnique()
            .HasDatabaseName("IX_CharacterOutboxMessages_EventId");

        // Index for deterministic pending query: (Status, OccurredAtUtc, CreatedAtUtc, Id)
        builder.HasIndex(m => new { m.Status, m.OccurredAtUtc, m.CreatedAtUtc })
            .HasDatabaseName("IX_CharacterOutboxMessages_Status_OccurredAtUtc_CreatedAtUtc");

        // Index for character-isolated queries
        builder.HasIndex(m => new { m.CharacterId, m.Status, m.OccurredAtUtc })
            .HasDatabaseName("IX_CharacterOutboxMessages_CharacterId_Status_OccurredAtUtc");
    }
}
