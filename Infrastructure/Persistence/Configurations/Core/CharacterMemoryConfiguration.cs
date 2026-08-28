using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class CharacterMemoryConfiguration : IEntityTypeConfiguration<CharacterMemory>
{
    public void Configure(EntityTypeBuilder<CharacterMemory> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedOnAdd();

        builder.Property(m => m.CharacterId).IsRequired();
        builder.Property(m => m.UserId).IsRequired();
        builder.Property(m => m.Content).IsRequired().HasMaxLength(1000);
        builder.Property(m => m.Type).IsRequired();
        builder.Property(m => m.Importance).IsRequired().HasDefaultValue(3);
        builder.Property(m => m.Confidence).HasPrecision(5, 4).HasDefaultValue(0.9m);
        builder.Property(m => m.EmbeddingJson).HasColumnType("jsonb").IsRequired(false);

        // Optimized composite indexes for retrieval queries
        builder.HasIndex(m => new { m.UserId, m.CharacterId })
               .HasDatabaseName("IX_CharacterMemories_UserId_CharacterId");

        builder.HasIndex(m => new { m.UserId, m.CharacterId, m.Importance })
               .HasDatabaseName("IX_CharacterMemories_UserId_CharacterId_Importance");

        builder.HasIndex(m => new { m.UserId, m.CharacterId, m.CreatedAt })
               .HasDatabaseName("IX_CharacterMemories_UserId_CharacterId_CreatedAt");
    }
}
