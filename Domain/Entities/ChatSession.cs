using Domain.Common;
using Domain.Enums;

namespace Domain.Entities;

public class ChatSession : BaseEntity
{
    public Guid CharacterId { get; private set; }
    public Guid? UserId { get; private set; }
    public string Title { get; private set; } = string.Empty;

    public int AffectionScore { get; private set; }
    public int RelationshipLevel { get; private set; }
    public string CurrentMood { get; private set; } = string.Empty;

    public List<ChatMessage> Messages { get; private set; } = [];

    private ChatSession() { } // EF Core

    public ChatSession(Guid characterId, Guid? userId, string title, string? initialMood = null, int initialAffection = 0)
    {
        CharacterId = characterId;
        UserId = userId;
        Title = title;
        AffectionScore = Math.Clamp(initialAffection, -100, 100);
        RelationshipLevel = CalculateRelationshipLevel(AffectionScore);
        CurrentMood = initialMood?.Trim() ?? string.Empty;
    }

    public (int newScore, int newLevel, int delta, bool levelUp) UpdateAffection(int delta, string? newMood = null)
    {
        var oldLevel = RelationshipLevel;
        AffectionScore = Math.Clamp(AffectionScore + delta, -100, 100);
        RelationshipLevel = CalculateRelationshipLevel(AffectionScore);

        if (!string.IsNullOrWhiteSpace(newMood))
        {
            CurrentMood = newMood.Trim();
        }

        var isLevelUp = RelationshipLevel > oldLevel;
        Touch();
        return (AffectionScore, RelationshipLevel, delta, isLevelUp);
    }

    private static int CalculateRelationshipLevel(int score) => score switch
    {
        <= -61 => -2, // Kẻ Thù Không Đội Trời Chung (Nemesis)
        <= -21 => -1, // Thù Địch & Ác Cảm (Hostile)
        <= 20 => 1,   // Người Lạ (Neutral / Stranger)
        <= 45 => 2,   // Người Quen (Acquaintance)
        <= 70 => 3,   // Bạn Thân Thiết (Close Friend)
        <= 90 => 4,   // Tri Kỷ & Rung Động (Soulmate / Romantic)
        _ => 5        // Gắn Kết Linh Hồn (Eternal Devotion)
    };

    public ChatMessage AddUserMessage(string content)
    {
        var message = new ChatMessage(Id, MessageRole.User, content);
        Messages.Add(message);
        Touch();
        return message;
    }

    public ChatMessage AddAssistantMessage(string content)
    {
        var message = new ChatMessage(Id, MessageRole.Assistant, content);
        Messages.Add(message);
        Touch();
        return message;
    }

    public void SetTitle(string title)
    {
        Title = title;
        Touch();
    }

    public List<ChatMessage> RollbackToMessage(Guid messageId)
    {
        var targetIndex = Messages.FindIndex(m => m.Id == messageId);
        if (targetIndex < 0 || targetIndex >= Messages.Count - 1)
        {
            return [];
        }

        var removed = Messages.Skip(targetIndex + 1).ToList();
        Messages.RemoveRange(targetIndex + 1, Messages.Count - (targetIndex + 1));
        Touch();
        return removed;
    }
}
