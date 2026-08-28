using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public class CharacterAutonomyTickConfiguration : IEntityTypeConfiguration<CharacterAutonomyTick>
{
    public void Configure(EntityTypeBuilder<CharacterAutonomyTick> builder)
    {
        builder.ToTable("CharacterAutonomyTicks");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CharacterId).IsRequired();
        builder.Property(x => x.ExecutionId).IsRequired();
        builder.Property(x => x.TimeBucket).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Status).IsRequired();
        builder.Property(x => x.DecisionFingerprint).HasMaxLength(128);
        builder.Property(x => x.CorrelationId).HasMaxLength(128);
        builder.Property(x => x.ErrorMessage).HasMaxLength(1024);

        builder.Property(x => x.StartedAt).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken();

        // Database Authoritative Unique Constraint: A character can execute an autonomy tick for a given TimeBucket exactly once!
        builder.HasIndex(x => new { x.CharacterId, x.TimeBucket })
            .IsUnique();

        builder.HasIndex(x => x.ExecutionId);
        builder.HasIndex(x => new { x.CharacterId, x.Status });
        builder.HasIndex(x => new { x.CharacterId, x.StartedAt });
    }
}
