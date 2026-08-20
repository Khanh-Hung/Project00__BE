using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class CharacterTurnConfiguration : IEntityTypeConfiguration<CharacterTurn>
{
    public void Configure(EntityTypeBuilder<CharacterTurn> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedOnAdd();

        builder.Property(t => t.TurnId).IsRequired();
        builder.Property(t => t.SessionId).IsRequired();
        builder.Property(t => t.UserId).IsRequired();
        builder.Property(t => t.CharacterId).IsRequired();
        builder.Property(t => t.UserMessage).IsRequired();
        builder.Property(t => t.AssistantReply).IsRequired();
        builder.Property(t => t.Mood).HasMaxLength(50);
        builder.Property(t => t.RelationshipStage).HasMaxLength(100);
        builder.Property(t => t.EventsJson).HasColumnType("jsonb");
        builder.Property(t => t.ActiveMemoriesJson).HasColumnType("jsonb");

        // Unique constraint on TurnId for strict persistent idempotency across servers and restarts
        builder.HasIndex(t => t.TurnId)
               .IsUnique()
               .HasDatabaseName("IX_CharacterTurns_TurnId");

        builder.HasIndex(t => new { t.SessionId, t.UserId });
    }
}
