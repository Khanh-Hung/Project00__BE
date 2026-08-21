using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public class AudioArtifactConfiguration : IEntityTypeConfiguration<AudioArtifact>
{
    public void Configure(EntityTypeBuilder<AudioArtifact> builder)
    {
        builder.ToTable("AudioArtifacts");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.SessionId).IsRequired();
        builder.Property(x => x.CharacterId).IsRequired();
        builder.Property(x => x.TurnId).IsRequired();
        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.VoiceId).IsRequired().HasMaxLength(256);
        builder.Property(x => x.CleanedText).IsRequired();
        builder.Property(x => x.ContextHash).IsRequired().HasMaxLength(128);
        builder.Property(x => x.AudioUrl).IsRequired().HasMaxLength(2048);
        builder.Property(x => x.AudioFormat).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Duration).IsRequired(false);

        // Invariant: ContextHash serves as the primary idempotency identity with DB UNIQUE constraint
        builder.HasIndex(x => x.ContextHash).IsUnique();
        builder.HasIndex(x => x.TurnId);
        builder.HasIndex(x => x.SessionId);
    }
}
