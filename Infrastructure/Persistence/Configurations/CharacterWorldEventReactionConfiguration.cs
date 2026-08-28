using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public class CharacterWorldEventReactionConfiguration : IEntityTypeConfiguration<CharacterWorldEventReaction>
{
    public void Configure(EntityTypeBuilder<CharacterWorldEventReaction> builder)
    {
        builder.ToTable("CharacterWorldEventReactions");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CharacterId).IsRequired();
        builder.Property(x => x.WorldEventId).IsRequired();
        builder.Property(x => x.ExecutionId).IsRequired();
        builder.Property(x => x.PerceptionType).IsRequired();
        builder.Property(x => x.Priority).IsRequired();
        builder.Property(x => x.ReactionReason).HasMaxLength(1024);

        // Database Authoritative Unique Constraint: A character can process a specific WorldEvent exactly once!
        builder.HasIndex(x => new { x.WorldEventId, x.CharacterId })
            .IsUnique();

        builder.HasIndex(x => new { x.CharacterId, x.ProcessedAt });
        builder.HasIndex(x => x.ExecutionId);
    }
}
