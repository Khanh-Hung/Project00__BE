using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public class CharacterGoalExecutionProgressConfiguration : IEntityTypeConfiguration<CharacterGoalExecutionProgress>
{
    public void Configure(EntityTypeBuilder<CharacterGoalExecutionProgress> builder)
    {
        builder.ToTable("CharacterGoalExecutionProgresses");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CharacterId).IsRequired();
        builder.Property(x => x.GoalId).IsRequired();
        builder.Property(x => x.ExecutionId).IsRequired();
        builder.Property(x => x.ActionType).IsRequired().HasMaxLength(128);
        builder.Property(x => x.OperationFingerprint).IsRequired().HasMaxLength(128);
        builder.Property(x => x.AppliedAtUtc).IsRequired();

        // Unique boundary: each (GoalId, ExecutionId) progress operation is applied at most once
        builder.HasIndex(x => new { x.GoalId, x.ExecutionId }).IsUnique();
        builder.HasIndex(x => new { x.CharacterId, x.ExecutionId });
    }
}
