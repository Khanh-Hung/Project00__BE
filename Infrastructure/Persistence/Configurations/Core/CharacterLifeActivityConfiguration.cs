using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class CharacterLifeActivityConfiguration : IEntityTypeConfiguration<CharacterLifeActivity>
{
    public void Configure(EntityTypeBuilder<CharacterLifeActivity> builder)
    {
        builder.ToTable("CharacterLifeActivities");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.CharacterId).IsRequired();

        builder.Property(a => a.ActivityType)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(a => a.Status)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(a => a.StartAtUtc).IsRequired();
        builder.Property(a => a.PlannedEndAtUtc).IsRequired();
        builder.Property(a => a.StartedAtUtc);
        builder.Property(a => a.CompletedAtUtc);
        builder.Property(a => a.CancellationReason).HasMaxLength(500);
        builder.Property(a => a.Metadata).HasMaxLength(2000);
        builder.Property(a => a.CreatedAtUtc).IsRequired();

        builder.Property(a => a.Version)
            .IsConcurrencyToken()
            .IsRequired();

        builder.HasIndex(a => new { a.CharacterId, a.Status })
            .HasDatabaseName("IX_CharacterLifeActivities_CharacterId_Status");

        builder.HasIndex(a => new { a.CharacterId, a.StartAtUtc })
            .HasDatabaseName("IX_CharacterLifeActivities_CharacterId_StartAtUtc");

        builder.HasIndex(a => new { a.CharacterId, a.PlannedEndAtUtc })
            .HasDatabaseName("IX_CharacterLifeActivities_CharacterId_PlannedEndAtUtc");
    }
}
