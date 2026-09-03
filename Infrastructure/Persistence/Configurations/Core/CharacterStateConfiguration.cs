using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class CharacterStateConfiguration : IEntityTypeConfiguration<CharacterState>
{
    public void Configure(EntityTypeBuilder<CharacterState> builder)
    {
        builder.ToTable("CharacterStates");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.CharacterId).IsRequired();
        builder.HasIndex(s => s.CharacterId).IsUnique();

        builder.Property(s => s.Hunger).HasPrecision(5, 2).IsRequired();
        builder.Property(s => s.Energy).HasPrecision(5, 2).IsRequired();
        builder.Property(s => s.Mood).HasPrecision(5, 2).IsRequired();
        builder.Property(s => s.Stress).HasPrecision(5, 2).IsRequired();
        builder.Property(s => s.SocialNeed).HasPrecision(5, 2).IsRequired();
        builder.Property(s => s.Comfort).HasPrecision(5, 2).IsRequired();

        builder.Property(s => s.LastEvolvedAtUtc).IsRequired();
        builder.Property(s => s.Version).IsConcurrencyToken().IsRequired();
    }
}
