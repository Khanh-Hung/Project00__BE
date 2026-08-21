using Domain.Common;
using Domain.Enums;

namespace Domain.Entities;

public class ChatSession : BaseEntity
{
    public Guid CharacterId { get; private set; }
    public Guid? UserId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public SessionStatus Status { get; private set; } = SessionStatus.Active;
    public DateTime? WalkedOutAt { get; private set; }
    public string? WalkOutReason { get; private set; }
    public Domain.ValueObjects.SessionSceneState? SceneState { get; private set; }

    public List<ChatMessage> Messages { get; private set; } = [];

    private ChatSession() { } // EF Core

    public ChatSession(Guid characterId, Guid? userId, string title, Domain.ValueObjects.SessionSceneState? sceneState = null)
    {
        CharacterId = characterId;
        UserId = userId;
        Title = title;
        Status = SessionStatus.Active;
        SceneState = sceneState;
    }

    public void UpdateSceneState(Domain.ValueObjects.SessionSceneState newState)
    {
        SceneState = newState;
        Touch();
    }

    public void WalkOut(string reason, DateTime timestamp)
    {
        Status = SessionStatus.WalkedOut;
        WalkOutReason = reason;
        WalkedOutAt = timestamp;
        Touch();
    }

    public void Reopen()
    {
        Status = SessionStatus.Active;
        WalkOutReason = null;
        WalkedOutAt = null;
        Touch();
    }

    public void Close()
    {
        Status = SessionStatus.Closed;
        Touch();
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
