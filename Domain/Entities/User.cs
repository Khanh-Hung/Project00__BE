using Domain.Common;

namespace Domain.Entities;

public class User : BaseEntity
{
    public string Email { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public string UserName { get; private set; } = string.Empty;
    public string AvatarUrl { get; private set; } = string.Empty;

    private User() { } // EF Core

    public User(string email, string passwordHash, string userName, string? avatarUrl = null)
    {
        Email = email.Trim().ToLowerInvariant();
        PasswordHash = passwordHash;
        UserName = userName.Trim();
        AvatarUrl = avatarUrl?.Trim() ?? string.Empty;
    }

    public void UpdateProfile(string userName, string avatarUrl)
    {
        UserName = userName.Trim();
        AvatarUrl = avatarUrl.Trim();
        Touch();
    }

    public void UpdatePassword(string newPasswordHash)
    {
        PasswordHash = newPasswordHash;
        Touch();
    }
}
