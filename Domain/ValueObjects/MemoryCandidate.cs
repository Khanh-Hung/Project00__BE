using Domain.Enums;

namespace Domain.ValueObjects;

public sealed record MemoryCandidate
{
    public const int MaxContentLength = 500;

    public string Content { get; init; }
    public MemoryType Type { get; init; }
    public int Importance { get; init; }
    public decimal Confidence { get; init; }

    public MemoryCandidate(string content, MemoryType type, int importance = 3, decimal confidence = 0.9m)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Memory candidate content cannot be null or empty.", nameof(content));
        }

        var trimmed = content.Trim();
        if (trimmed.Length > MaxContentLength)
        {
            throw new ArgumentException($"Memory candidate content cannot exceed {MaxContentLength} characters.", nameof(content));
        }

        if (importance is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(importance), "Importance must be between 1 and 5.");
        }

        if (confidence is < 0.0m or > 1.0m)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be between 0.0 and 1.0.");
        }

        Content = trimmed;
        Type = type;
        Importance = importance;
        Confidence = confidence;
    }
}
