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
        EventKey = trimmedKey.Length > MaxEventKeyLength ? trimmedKey.Substring(0, MaxEventKeyLength) : trimmedKey;

        var trimmedContext = context.Trim();
        Context = trimmedContext.Length > MaxContextLength ? trimmedContext.Substring(0, MaxContextLength) : trimmedContext;

        UnlockedAt = unlockedAt;
    }
}
