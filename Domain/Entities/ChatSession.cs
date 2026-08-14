using Domain.Common;
using Domain.Enums;

namespace Domain.Entities;

public class ChatSession : BaseEntity
{
    public Guid CharacterId { get; private set; }
    public Guid UserId { get; private set; }
    public string Title { get; private set; } = string.Empty;

    public List<ChatMessage> Messages { get; private set; } = [];

    private ChatSession() { } // EF Core

    public ChatSession(Guid characterId, Guid userId, string title)
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

    public ChatMessage AddAssistantMessage(string content, int tokensUsed = 0)
    {
        var message = new ChatMessage(Id, MessageRole.Assistant, content, tokensUsed);
        Messages.Add(message);
        Touch();
        return message;
    }

    public void SetTitle(string title)
    {
        Title = title;
        Touch();
    }
}
