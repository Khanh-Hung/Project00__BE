using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public class GoalActivityContributionConfiguration : IEntityTypeConfiguration<GoalActivityContribution>
{
    public void Configure(EntityTypeBuilder<GoalActivityContribution> builder)
    {
        builder.ToTable("GoalActivityContributions");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.GoalId).IsRequired();
        builder.Property(x => x.ActivityId).IsRequired();

        // Unique constraint: An activity can contribute to a specific goal exactly once
        builder.HasIndex(x => new { x.GoalId, x.ActivityId }).IsUnique();
    }
}
