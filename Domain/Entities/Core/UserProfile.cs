using Domain.Common;
using System.Text.Json;

namespace Domain.Entities;

public class UserProfile : BaseEntity
{
    public Guid UserId { get; private set; }
    public string? Bio { get; private set; }
    public string InterestsJson { get; private set; } = "[]";
    public string PersonalityTraitsJson { get; private set; } = "[]";
    public string? StatusMessage { get; private set; }

    private UserProfile() { } // EF Core

    public UserProfile(
        Guid userId,
        string? bio = null,
        List<string>? interests = null,
        List<string>? personalityTraits = null,
        string? statusMessage = null)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        Bio = bio?.Trim();
        InterestsJson = JsonSerializer.Serialize(interests ?? new List<string>());
        PersonalityTraitsJson = JsonSerializer.Serialize(personalityTraits ?? new List<string>());
        StatusMessage = statusMessage?.Trim();
        CreatedAt = DateTime.UtcNow;
    }

    public static UserProfile Create(
        Guid userId,
        string? bio = null,
        List<string>? interests = null,
        List<string>? personalityTraits = null,
        string? statusMessage = null)
    {
        return new UserProfile(userId, bio, interests, personalityTraits, statusMessage);
    }

    public void Update(
        string? bio,
        List<string>? interests,
        List<string>? personalityTraits,
        string? statusMessage,
        DateTime updatedAt)
    {
        Bio = bio?.Trim();
        InterestsJson = JsonSerializer.Serialize(interests ?? new List<string>());
        PersonalityTraitsJson = JsonSerializer.Serialize(personalityTraits ?? new List<string>());
        StatusMessage = statusMessage?.Trim();
        UpdatedAt = updatedAt;
    }

    public List<string> GetInterests()
    {
        if (string.IsNullOrWhiteSpace(InterestsJson)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(InterestsJson) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    public List<string> GetPersonalityTraits()
    {
        if (string.IsNullOrWhiteSpace(PersonalityTraitsJson)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(PersonalityTraitsJson) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}
