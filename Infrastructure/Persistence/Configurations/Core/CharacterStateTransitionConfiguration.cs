using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class CharacterStateTransitionConfiguration : IEntityTypeConfiguration<CharacterStateTransition>
{
    public void Configure(EntityTypeBuilder<CharacterStateTransition> builder)
    {
        builder.ToTable("CharacterStateTransitions");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.CharacterId).IsRequired();
        builder.Property(t => t.ExecutionId).IsRequired();
        builder.HasIndex(t => new { t.CharacterId, t.ExecutionId }).IsUnique();
        builder.HasIndex(t => new { t.CharacterId, t.AppliedAtUtc });

        builder.Property(t => t.SourceType).HasMaxLength(50).IsRequired();
        builder.Property(t => t.SourceId).HasMaxLength(100);
        builder.Property(t => t.TransitionFingerprint).HasMaxLength(64).IsRequired();

        builder.Property(t => t.HungerDelta).HasPrecision(5, 2).IsRequired();
        builder.Property(t => t.EnergyDelta).HasPrecision(5, 2).IsRequired();
        builder.Property(t => t.MoodDelta).HasPrecision(5, 2).IsRequired();
        builder.Property(t => t.StressDelta).HasPrecision(5, 2).IsRequired();
        builder.Property(t => t.SocialNeedDelta).HasPrecision(5, 2).IsRequired();
        builder.Property(t => t.ComfortDelta).HasPrecision(5, 2).IsRequired();

        builder.Property(t => t.VersionBefore).IsRequired();
        builder.Property(t => t.VersionAfter).IsRequired();
        builder.Property(t => t.AppliedAtUtc).IsRequired();
    }
}
