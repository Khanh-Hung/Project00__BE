using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public class SceneVisualStateRecordConfiguration : IEntityTypeConfiguration<SceneVisualStateRecord>
{
    public void Configure(EntityTypeBuilder<SceneVisualStateRecord> builder)
    {
        builder.ToTable("SceneVisualStates");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.SessionId).IsRequired();
        builder.Property(x => x.CharacterId).IsRequired();
        builder.Property(x => x.SceneKey).IsRequired().HasMaxLength(256);
        builder.Property(x => x.SceneRevision).IsRequired().HasDefaultValue(1);

        builder.Property(x => x.StateJson).IsRequired();
        builder.Property(x => x.Fingerprint).IsRequired().HasMaxLength(64);

        builder.Property(x => x.SourceTurnId).IsRequired(false);
        builder.Property(x => x.ValidFromTurnId).IsRequired(false);
        builder.Property(x => x.ValidUntilTurnId).IsRequired(false);

        builder.Property(x => x.Version).IsConcurrencyToken();

        builder.HasIndex(x => new { x.SessionId, x.SceneKey, x.SceneRevision });
        builder.HasIndex(x => new { x.SessionId, x.CharacterId, x.SceneRevision });
        builder.HasIndex(x => x.Fingerprint);
        builder.HasIndex(x => x.CharacterId);
        builder.HasIndex(x => x.SessionId);
    }
}
