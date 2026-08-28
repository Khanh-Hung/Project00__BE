using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class ChatSessionConfiguration : IEntityTypeConfiguration<ChatSession>
{
    public void Configure(EntityTypeBuilder<ChatSession> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Title).HasMaxLength(200);
        builder.Property(s => s.Status).IsRequired().HasDefaultValue(SessionStatus.Active);
        builder.Property(s => s.WalkOutReason).HasMaxLength(1000).IsRequired(false);
        builder.Property(s => s.WalkedOutAt).IsRequired(false);

        builder.Property(s => s.SceneState)
            .HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => string.IsNullOrWhiteSpace(v) ? null : System.Text.Json.JsonSerializer.Deserialize<Domain.ValueObjects.SessionSceneState>(v, (System.Text.Json.JsonSerializerOptions?)null)
            );

        builder.HasMany(s => s.Messages)
               .WithOne()
               .HasForeignKey(m => m.ChatSessionId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(s => s.Messages).AutoInclude();
    }
}
