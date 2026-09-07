using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class CharacterPersonalityConfiguration : IEntityTypeConfiguration<CharacterPersonality>
{
    public void Configure(EntityTypeBuilder<CharacterPersonality> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();

        builder.Property(p => p.CharacterId).IsRequired();

        builder.Property(p => p.Warmth).IsRequired().HasDefaultValue(CharacterPersonality.DefaultTraitValue);
        builder.Property(p => p.Openness).IsRequired().HasDefaultValue(CharacterPersonality.DefaultTraitValue);
        builder.Property(p => p.Assertiveness).IsRequired().HasDefaultValue(CharacterPersonality.DefaultTraitValue);
        builder.Property(p => p.Conscientiousness).IsRequired().HasDefaultValue(CharacterPersonality.DefaultTraitValue);
        builder.Property(p => p.SocialConfidence).IsRequired().HasDefaultValue(CharacterPersonality.DefaultTraitValue);
        builder.Property(p => p.TrustDisposition).IsRequired().HasDefaultValue(CharacterPersonality.DefaultTraitValue);
        builder.Property(p => p.EmotionalStability).IsRequired().HasDefaultValue(CharacterPersonality.DefaultTraitValue);

        builder.Property(p => p.Version).IsConcurrencyToken().IsRequired().HasDefaultValue(1u);

        builder.HasIndex(p => p.CharacterId)
               .IsUnique()
               .HasDatabaseName("IX_CharacterPersonalities_CharacterId");
    }
}
