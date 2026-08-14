using Domain.Common;

namespace Domain.Entities;

public class User : BaseEntity
{
    public string Email { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public string FullName { get; private set; } = string.Empty;
    public string AvatarUrl { get; private set; } = string.Empty;

    private User() { } // EF Core

    public User(string email, string passwordHash, string fullName, string? avatarUrl = null)
    {
        Email = email.Trim().ToLowerInvariant();
        PasswordHash = passwordHash;
        FullName = fullName.Trim();
        AvatarUrl = avatarUrl?.Trim() ?? string.Empty;
    }

    public void UpdateProfile(string fullName, string avatarUrl)
    {
        FullName = fullName.Trim();
        AvatarUrl = avatarUrl.Trim();
        Touch();
    }

    public void UpdatePassword(string newPasswordHash)
    {
        PasswordHash = newPasswordHash;
        Touch();
    }
}
