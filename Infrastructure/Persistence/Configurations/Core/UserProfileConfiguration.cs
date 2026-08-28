using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class UserProfileConfiguration : IEntityTypeConfiguration<UserProfile>
{
    public void Configure(EntityTypeBuilder<UserProfile> builder)
    {
        builder.ToTable("UserProfiles");
        builder.HasKey(p => p.Id);
        builder.HasIndex(p => p.UserId).IsUnique();

        builder.Property(p => p.DisplayName).HasMaxLength(100).IsRequired();
        builder.Property(p => p.AvatarUrl).HasMaxLength(500);
        builder.Property(p => p.Bio).HasMaxLength(500);
        builder.Property(p => p.InterestsJson).HasMaxLength(1000).IsRequired();
        builder.Property(p => p.PersonalityTraitsJson).HasMaxLength(1000).IsRequired();
        builder.Property(p => p.StatusMessage).HasMaxLength(200);
    }
}
