using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class CharacterSocialPresenceTransitionConfiguration : IEntityTypeConfiguration<CharacterSocialPresenceTransition>
{
    public void Configure(EntityTypeBuilder<CharacterSocialPresenceTransition> builder)
    {
        builder.ToTable("CharacterSocialPresenceTransitions");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.CharacterId).IsRequired();
        builder.Property(x => x.ExecutionId).IsRequired();

        builder.HasIndex(x => new { x.CharacterId, x.ExecutionId })
            .IsUnique()
            .HasDatabaseName("IX_CharacterSocialPresenceTransitions_CharacterId_ExecutionId");

        builder.Property(x => x.ActionType)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.OldStatus)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.NewStatus)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.OldActivityType)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.NewActivityType)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.TargetType)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(x => x.TargetId);

        builder.Property(x => x.VersionBefore).IsRequired();
        builder.Property(x => x.VersionAfter).IsRequired();

        builder.Property(x => x.OperationFingerprint)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(x => x.AppliedAtUtc).IsRequired();
        builder.HasIndex(x => new { x.CharacterId, x.AppliedAtUtc });
    }
}
