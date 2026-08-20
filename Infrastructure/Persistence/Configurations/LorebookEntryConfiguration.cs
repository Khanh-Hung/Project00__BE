using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class LorebookEntryConfiguration : IEntityTypeConfiguration<LorebookEntry>
{
    public void Configure(EntityTypeBuilder<LorebookEntry> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        builder.Property(e => e.Title).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Content).IsRequired();
        builder.Property(e => e.Category).IsRequired().HasConversion<string>().HasMaxLength(50);
        builder.Property(e => e.Priority).IsRequired().HasDefaultValue(100);
        builder.Property(e => e.IsConstant).IsRequired().HasDefaultValue(false);
        builder.Property(e => e.IsEnabled).IsRequired().HasDefaultValue(true);

        builder.Property(e => e.Keywords)
            .HasColumnType("jsonb")
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => string.IsNullOrWhiteSpace(v)
                    ? new List<string>()
                    : System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<string>()
            );

        builder.HasIndex(e => new { e.CharacterId, e.IsEnabled })
               .HasDatabaseName("IX_LorebookEntries_CharacterId_IsEnabled");
    }
}
