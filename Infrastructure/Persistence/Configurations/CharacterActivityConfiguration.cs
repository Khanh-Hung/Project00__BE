using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public class CharacterActivityConfiguration : IEntityTypeConfiguration<CharacterActivity>
{
    public void Configure(EntityTypeBuilder<CharacterActivity> builder)
    {
        builder.ToTable("CharacterActivities");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CharacterId).IsRequired();
        builder.Property(x => x.Location).IsRequired().HasMaxLength(256);
        builder.Property(x => x.TimeBucket).IsRequired().HasMaxLength(64);
        builder.Property(x => x.DecisionFingerprint).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Reason).HasMaxLength(1024);

        builder.Property(x => x.Version).IsConcurrencyToken();

        // Unique constraint: Exactly one autonomous activity per character per time bucket
        builder.HasIndex(x => new { x.CharacterId, x.TimeBucket })
            .HasFilter("\"Source\" = 'Autonomous' OR \"Source\" = 1")
            .IsUnique();

        builder.HasIndex(x => new { x.CharacterId, x.Status });
        builder.HasIndex(x => new { x.CharacterId, x.CreatedAt });
        builder.HasIndex(x => x.SceneIntentId);
    }
}
