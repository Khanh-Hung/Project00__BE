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

        builder.Property(x => x.HairDescription).HasMaxLength(1024);
        builder.Property(x => x.EyeDescription).HasMaxLength(1024);
        builder.Property(x => x.SkinDescription).HasMaxLength(1024);
        builder.Property(x => x.BodyDescription).HasMaxLength(1024);
        builder.Property(x => x.DistinguishingFeatures).HasMaxLength(2048);

        builder.Property(x => x.PrimaryReferenceId).IsRequired(false);
        builder.Property(x => x.FaceReferenceId).IsRequired(false);

        // One Character has exactly one authoritative CharacterVisualProfile
        builder.HasIndex(x => x.CharacterId).IsUnique();
    }
}
