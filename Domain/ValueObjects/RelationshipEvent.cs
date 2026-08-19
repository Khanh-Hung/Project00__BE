namespace Domain.ValueObjects;

public sealed record RelationshipEvent
{
    public const int MaxEventKeyLength = 100;
    public const int MaxContextLength = 500;

    public string EventKey { get; init; }
    public string Context { get; init; }
    public DateTime UnlockedAt { get; init; }

    public RelationshipEvent(string eventKey, string context, DateTime unlockedAt)
    {
        if (string.IsNullOrWhiteSpace(eventKey))
        {
            throw new ArgumentException("EventKey cannot be null or empty.", nameof(eventKey));
        }

        if (string.IsNullOrWhiteSpace(context))
        {
            throw new ArgumentException("Context cannot be null or empty.", nameof(context));
        }

        var trimmedKey = eventKey.Trim();
        if (trimmedKey.Length > MaxEventKeyLength)
        {
            throw new ArgumentException($"EventKey cannot exceed {MaxEventKeyLength} characters.", nameof(eventKey));
        }

        var trimmedContext = context.Trim();
        if (trimmedContext.Length > MaxContextLength)
        {
            throw new ArgumentException($"Context cannot exceed {MaxContextLength} characters.", nameof(context));
        }

        EventKey = trimmedKey;
        Context = trimmedContext;
        UnlockedAt = unlockedAt;
    }
}
