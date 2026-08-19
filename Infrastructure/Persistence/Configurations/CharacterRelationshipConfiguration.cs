using Domain.Entities;
using Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class CharacterRelationshipConfiguration : IEntityTypeConfiguration<CharacterRelationship>
{
    public void Configure(EntityTypeBuilder<CharacterRelationship> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedOnAdd();

        builder.Property(r => r.CharacterId).IsRequired();
        builder.Property(r => r.UserId).IsRequired();
        builder.Property(r => r.AffectionScore).IsRequired().HasDefaultValue(0);
        builder.Property(r => r.CurrentMood).IsRequired().HasConversion<string>().HasMaxLength(50);
        builder.Property(r => r.MoodIntensity).IsRequired().HasDefaultValue(20);
        builder.Property(r => r.LastInteractedAt).IsRequired();

        builder.Property(r => r.Events)
            .HasField("_events")
            .HasColumnName("EventsJson")
            .HasColumnType("jsonb")
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => string.IsNullOrWhiteSpace(v)
                    ? new List<RelationshipEvent>()
                    : System.Text.Json.JsonSerializer.Deserialize<List<RelationshipEvent>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<RelationshipEvent>()
            );

        // Unique composite index: (UserId, CharacterId)
        builder.HasIndex(r => new { r.UserId, r.CharacterId })
               .IsUnique()
               .HasDatabaseName("IX_CharacterRelationships_UserId_CharacterId");
    }
}
