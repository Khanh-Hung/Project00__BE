using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedOnAdd();

        builder.Property(o => o.EventType).IsRequired().HasMaxLength(100);
        builder.Property(o => o.PayloadJson).IsRequired().HasColumnType("jsonb");
        builder.Property(o => o.Status).IsRequired().HasConversion<string>().HasMaxLength(50);
        builder.Property(o => o.RetryCount).IsRequired().HasDefaultValue(0);
        builder.Property(o => o.MaxRetries).IsRequired().HasDefaultValue(3);

        builder.HasIndex(o => new { o.Status, o.CreatedAt })
               .HasDatabaseName("IX_OutboxMessages_Status_CreatedAt");
    }
}
