using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public class CharacterGoalConfiguration : IEntityTypeConfiguration<CharacterGoal>
{
    public void Configure(EntityTypeBuilder<CharacterGoal> builder)
    {
        builder.ToTable("CharacterGoals");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CharacterId).IsRequired();
        builder.Property(x => x.Title).IsRequired().HasMaxLength(256);
        builder.Property(x => x.Description).HasMaxLength(1024);

        builder.Property(x => x.GoalType)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(x => x.Status)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(x => x.Priority)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(x => x.Version).IsConcurrencyToken();

        builder.HasMany(x => x.Milestones)
            .WithOne()
            .HasForeignKey(x => x.GoalId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.CharacterId, x.Status });
        builder.HasIndex(x => new { x.CharacterId, x.Priority });
        builder.HasIndex(x => new { x.CharacterId, x.CreatedAt });
    }
}
