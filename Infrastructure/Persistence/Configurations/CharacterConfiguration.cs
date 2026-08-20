using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class CharacterConfiguration : IEntityTypeConfiguration<Character>
{
    public void Configure(EntityTypeBuilder<Character> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedOnAdd();
        builder.Property(c => c.Name).IsRequired().HasMaxLength(100);
        builder.Property(c => c.Title).HasMaxLength(200);
        builder.Property(c => c.PersonalityPrompt).IsRequired();
        builder.Property(c => c.Greeting).HasMaxLength(1000);
        builder.Property(c => c.DefaultAffectionScore).HasDefaultValue(0);
        builder.Property(c => c.DefaultMood).HasMaxLength(100);

        builder.Property(c => c.Blueprint)
            .HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => string.IsNullOrWhiteSpace(v) ? null : System.Text.Json.JsonSerializer.Deserialize<Domain.ValueObjects.CharacterBlueprint>(v, (System.Text.Json.JsonSerializerOptions?)null)
            );

        builder.Property(c => c.VisualIdentity)
            .HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => string.IsNullOrWhiteSpace(v) ? null : System.Text.Json.JsonSerializer.Deserialize<Domain.ValueObjects.CharacterVisualIdentity>(v, (System.Text.Json.JsonSerializerOptions?)null)
            );
    }
}
