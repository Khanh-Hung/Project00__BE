using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class ChatSessionConfiguration : IEntityTypeConfiguration<ChatSession>
{
    public void Configure(EntityTypeBuilder<ChatSession> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.AffectionScore).HasDefaultValue(0);
        builder.Property(s => s.RelationshipLevel).HasDefaultValue(1);
        builder.Property(s => s.CurrentMood).HasMaxLength(100);

        builder.HasMany(s => s.Messages)
               .WithOne()
               .HasForeignKey(m => m.ChatSessionId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(s => s.Messages).AutoInclude();
    }
}
