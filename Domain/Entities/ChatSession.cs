using Domain.Common;
using Domain.Enums;

namespace Domain.Entities;

public class ChatSession : BaseEntity
{
    public Guid CharacterId { get; private set; }
    public Guid? UserId { get; private set; }
    public string Title { get; private set; } = string.Empty;

    public List<ChatMessage> Messages { get; private set; } = [];

    private ChatSession() { } // EF Core

    public ChatSession(Guid characterId, Guid? userId, string title)
    {
        CharacterId = characterId;
        UserId = userId;
        Title = title;
    }

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
