using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public class CharacterWorldEventConfiguration : IEntityTypeConfiguration<CharacterWorldEvent>
{
    public void Configure(EntityTypeBuilder<CharacterWorldEvent> builder)
    {
        builder.ToTable("CharacterWorldEvents");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CharacterId).IsRequired();
        builder.Property(x => x.EventType).IsRequired();
        builder.Property(x => x.SourceType).IsRequired().HasMaxLength(64);
        builder.Property(x => x.SourceId).HasMaxLength(128);
        builder.Property(x => x.OccurredAt).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(128);

        builder.Property(x => x.Version).IsConcurrencyToken();

        builder.HasIndex(x => new { x.CharacterId, x.OccurredAt });
        builder.HasIndex(x => new { x.CharacterId, x.EventType });
        builder.HasIndex(x => x.CorrelationId);
    }
}
