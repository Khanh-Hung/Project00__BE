using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class CharacterSocialPresenceConfiguration : IEntityTypeConfiguration<CharacterSocialPresence>
{
    public void Configure(EntityTypeBuilder<CharacterSocialPresence> builder)
    {
        builder.ToTable("CharacterSocialPresences");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.CharacterId).IsRequired();
        builder.HasIndex(x => x.CharacterId).IsUnique();

        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.CurrentActivityType)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.TargetType)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(x => x.TargetId);

        builder.Property(x => x.Visibility)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.StartedAtUtc).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).IsRequired();

        builder.Property(x => x.Version)
            .IsConcurrencyToken()
            .IsRequired()
            .HasDefaultValue(1u);
    }
}
