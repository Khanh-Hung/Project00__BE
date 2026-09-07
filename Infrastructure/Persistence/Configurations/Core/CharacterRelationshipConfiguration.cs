using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class CharacterRelationshipConfiguration : IEntityTypeConfiguration<CharacterRelationship>
{
    public void Configure(EntityTypeBuilder<CharacterRelationship> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedOnAdd();

        builder.Property(r => r.CharacterId).IsRequired();
        builder.Property(r => r.TargetType).IsRequired().HasConversion<string>().HasMaxLength(50).HasDefaultValue(RelationshipTargetType.User);
        builder.Property(r => r.TargetId).IsRequired();
        builder.Property(r => r.RelationshipType).IsRequired().HasConversion<string>().HasMaxLength(50).HasDefaultValue(RelationshipType.Stranger);

        builder.Property(r => r.Trust).IsRequired().HasDefaultValue(0);
        builder.Property(r => r.Affection).IsRequired().HasDefaultValue(0);
        builder.Property(r => r.Familiarity).IsRequired().HasDefaultValue(0);

        // Backward-compatible properties:
        builder.Property(r => r.UserId).IsRequired();
        builder.Property(r => r.AffectionScore).IsRequired().HasDefaultValue(0);
        builder.Property(r => r.CurrentMood).IsRequired().HasConversion<string>().HasMaxLength(50);
        builder.Property(r => r.MoodIntensity).IsRequired().HasDefaultValue(20);
        builder.Property(r => r.LastInteractedAt).IsRequired();
        builder.Property(r => r.Version).IsConcurrencyToken().IsRequired().HasDefaultValue(1u);

        var eventsComparer = new ValueComparer<IReadOnlyCollection<RelationshipEvent>>(
            (c1, c2) => (c1 == null && c2 == null) || (c1 != null && c2 != null && c1.SequenceEqual(c2)),
            c => c == null ? 0 : c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
            c => c == null ? new List<RelationshipEvent>() : c.ToList());

        builder.Property(r => r.Events)
            .HasField("_events")
            .HasColumnName("EventsJson")
            .HasColumnType("jsonb")
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => string.IsNullOrWhiteSpace(v)
                    ? new List<RelationshipEvent>()
                    : System.Text.Json.JsonSerializer.Deserialize<List<RelationshipEvent>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<RelationshipEvent>()
            )
            .Metadata.SetValueComparer(eventsComparer);

        // PR48 Database uniqueness boundary: (CharacterId, TargetType, TargetId)
        builder.HasIndex(r => new { r.CharacterId, r.TargetType, r.TargetId })
               .IsUnique()
               .HasDatabaseName("IX_CharacterRelationships_CharacterId_TargetType_TargetId");

        // Existing composite index retained for legacy chat lookups:
        builder.HasIndex(r => new { r.UserId, r.CharacterId })
               .HasDatabaseName("IX_CharacterRelationships_UserId_CharacterId");
    }
}
