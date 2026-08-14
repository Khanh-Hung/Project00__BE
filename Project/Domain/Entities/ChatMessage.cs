using Domain.Common;
using Domain.Enums;

namespace Domain.Entities;

public class ChatMessage : BaseEntity
{
    public Guid ChatSessionId { get; private set; }
    public MessageRole Role { get; private set; }
    public string Content { get; private set; } = string.Empty;
    public int TokensUsed { get; private set; }

    private ChatMessage() { } // EF Core

    public ChatMessage(Guid chatSessionId, MessageRole role, string content, int tokensUsed = 0)
    {
        ChatSessionId = chatSessionId;
        Role = role;
        Content = content;
        TokensUsed = tokensUsed;
    }
}
