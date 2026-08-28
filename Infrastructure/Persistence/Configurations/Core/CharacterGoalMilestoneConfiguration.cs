using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public class CharacterGoalMilestoneConfiguration : IEntityTypeConfiguration<CharacterGoalMilestone>
{
    public void Configure(EntityTypeBuilder<CharacterGoalMilestone> builder)
    {
        builder.ToTable("CharacterGoalMilestones");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.GoalId).IsRequired();
        builder.Property(x => x.Title).IsRequired().HasMaxLength(256);
        builder.Property(x => x.Description).HasMaxLength(1024);

        builder.Property(x => x.Status)
            .HasConversion<string>()
            .IsRequired();

        builder.HasIndex(x => new { x.GoalId, x.Order });
    }
}
