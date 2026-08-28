using System.Text.RegularExpressions;
using Domain.Common;
using Domain.Common.DateTimes;
using Domain.Enums;

namespace Domain.Entities;

public class User : BaseEntity
{
    public string Email { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public string UserName { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string AvatarUrl { get; private set; } = string.Empty;
    public UserRole Role { get; private set; }
    public bool IsEmailVerified { get; private set; }
    public DateTime? LastUserNameChangedAt { get; private set; }

    private User() { } // EF Core

    public User(
        string email,
        string passwordHash,
        string userName,
        string? displayName = null,
        string? avatarUrl = null,
        UserRole role = UserRole.User,
        bool isEmailVerified = false)
    {
        Email = email.Trim().ToLowerInvariant();
        PasswordHash = passwordHash;
        UserName = NormalizeUserName(userName);
        DisplayName = displayName?.Trim() ?? string.Empty;
        AvatarUrl = avatarUrl?.Trim() ?? string.Empty;
        Role = role;
        IsEmailVerified = isEmailVerified;
    }

    public static string NormalizeUserName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "user_" + Guid.NewGuid().ToString("N")[..6];
        var cleaned = Regex.Replace(raw.Trim().ToLowerInvariant(), @"[^a-z0-9_]", "");
        if (cleaned.Length < 3) cleaned = cleaned + "_" + Guid.NewGuid().ToString("N")[..4];
        if (cleaned.Length > 30) cleaned = cleaned[..30];
        return cleaned;
    }

    public void UpdateProfile(string displayName, string avatarUrl)
    {
        DisplayName = displayName?.Trim() ?? string.Empty;
        AvatarUrl = avatarUrl?.Trim() ?? string.Empty;
        Touch();
    }

    public void VerifyEmail()
    {
        IsEmailVerified = true;
        Touch();
    }

    public void UpdateRole(UserRole newRole)
    {
        Role = newRole;
        Touch();
    }

    public bool CanChangeUserName(int cooldownDays = 14)
    {
        if (!LastUserNameChangedAt.HasValue) return true;
        return Clock.Now >= LastUserNameChangedAt.Value.AddDays(cooldownDays);
    }

    public DateTime? GetNextUserNameChangeDate(int cooldownDays = 14)
    {
        if (!LastUserNameChangedAt.HasValue) return null;
        var nextDate = LastUserNameChangedAt.Value.AddDays(cooldownDays);
        return Clock.Now >= nextDate ? null : nextDate;
    }

    public void UpdateUserName(string newUserName, int cooldownDays = 14)
    {
        if (!CanChangeUserName(cooldownDays))
        {
            throw new InvalidOperationException($"You can only change your UserName once every {cooldownDays} days.");
        }

        var normalized = NormalizeUserName(newUserName);
        if (normalized.Length < 3 || normalized.Length > 30)
        {
            throw new ArgumentException("UserName must be between 3 and 30 characters.");
        }

        UserName = normalized;
        LastUserNameChangedAt = Clock.Now;
        Touch();
    }

    public void UpdatePassword(string newPasswordHash)
    {
        PasswordHash = newPasswordHash;
        Touch();
    }
}
