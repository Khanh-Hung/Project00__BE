using System.Text.Json;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public class SceneSpecificationConfiguration : IEntityTypeConfiguration<SceneSpecification>
{
    public void Configure(EntityTypeBuilder<SceneSpecification> builder)
    {
        builder.ToTable("SceneSpecifications");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CharacterId).IsRequired();
        builder.Property(x => x.SessionId).IsRequired(false);
        builder.Property(x => x.TurnId).IsRequired(false);
        builder.Property(x => x.SceneRevision).IsRequired().HasDefaultValue(1);

        builder.Property(x => x.Location).IsRequired().HasMaxLength(1024);
        builder.Property(x => x.Action).IsRequired().HasMaxLength(2048);

        builder.Property(x => x.Pose).HasMaxLength(1024);
        builder.Property(x => x.Environment).HasMaxLength(2048);
        builder.Property(x => x.Lighting).HasMaxLength(1024);
        builder.Property(x => x.Camera).HasMaxLength(1024);
        builder.Property(x => x.Weather).HasMaxLength(512);
        builder.Property(x => x.TimeOfDay).HasMaxLength(512);
        builder.Property(x => x.Mood).HasMaxLength(512);
        builder.Property(x => x.OutfitContext).HasMaxLength(1024);

        var listComparer = new ValueComparer<IReadOnlyList<string>>(
            (c1, c2) => (c1 == null && c2 == null) || (c1 != null && c2 != null && c1.SequenceEqual(c2)),
            c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
            c => c.ToList()
        );

        builder.Property(x => x.Objects)
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>()
            )
            .Metadata.SetValueComparer(listComparer);

        builder.Property(x => x.AtmosphereElements)
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>()
            )
            .Metadata.SetValueComparer(listComparer);

        // Unique constraint for canonical scene specification per (CharacterId, SessionId, TurnId, SceneRevision)
        builder.HasIndex(x => new { x.CharacterId, x.SessionId, x.TurnId, x.SceneRevision })
            .HasFilter("\"SessionId\" IS NOT NULL AND \"TurnId\" IS NOT NULL")
            .IsUnique();

        builder.HasIndex(x => x.CharacterId);
        builder.HasIndex(x => x.SessionId);
    }
}
