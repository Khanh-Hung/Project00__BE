using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations.Core;

public sealed class CharacterAutonomousLifeTickConfiguration : IEntityTypeConfiguration<CharacterAutonomousLifeTick>
{
    public void Configure(EntityTypeBuilder<CharacterAutonomousLifeTick> builder)
    {
        builder.ToTable("CharacterAutonomousLifeTicks");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.CharacterId).IsRequired();
        builder.Property(x => x.SimulationTickId).IsRequired();
        builder.Property(x => x.SimulationTimeUtc).IsRequired();
        builder.Property(x => x.CycleId).IsRequired();
        builder.Property(x => x.ExecutionId).IsRequired();
        builder.Property(x => x.EventId).IsRequired();
        builder.Property(x => x.State).IsRequired();
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken();

        // Unique constraint: exactly one autonomous execution per (CharacterId, SimulationTickId)
        builder.HasIndex(x => new { x.CharacterId, x.SimulationTickId })
            .IsUnique();
    }
}
