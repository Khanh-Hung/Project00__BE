using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public class CharacterVisualProfileConfiguration : IEntityTypeConfiguration<CharacterVisualProfile>
{
    public void Configure(EntityTypeBuilder<CharacterVisualProfile> builder)
    {
        builder.ToTable("CharacterVisualProfiles");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CharacterId).IsRequired();
        builder.Property(x => x.VisualVersion).IsRequired().HasDefaultValue(1).IsConcurrencyToken();

        // Core Immutable Identity Traits
        builder.Property(x => x.EyeColor).HasMaxLength(1024);
        builder.Property(x => x.HairColor).HasMaxLength(1024);
        builder.Property(x => x.SkinTone).HasMaxLength(1024);
        builder.Property(x => x.FacialFeatures).HasMaxLength(1024);
        builder.Property(x => x.PermanentMarks).HasMaxLength(1024);
        builder.Property(x => x.BodyIdentity).HasMaxLength(1024);

        // Mutable Appearance Traits
        builder.Property(x => x.Hairstyle).HasMaxLength(1024);
        builder.Property(x => x.CurrentOutfit).HasMaxLength(1024);
        builder.Property(x => x.Makeup).HasMaxLength(1024);
        builder.Property(x => x.Accessories).HasMaxLength(1024);
        builder.Property(x => x.TemporaryAppearance).HasMaxLength(2048);

        builder.Property(x => x.PrimaryReferenceId).IsRequired(false);
        builder.Property(x => x.FaceReferenceId).IsRequired(false);

        // One Character has exactly one authoritative CharacterVisualProfile
        builder.HasIndex(x => x.CharacterId).IsUnique();
    }
}
