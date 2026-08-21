global using VoiceGenerationRequest = Application.Interfaces.VoiceProviderRequest;
using Domain.ValueObjects;

namespace Application.Interfaces;

public record VoiceProviderRequest(
    string CleanedText,
    string VoiceId,
    string? Language = "vi-VN",
    VoiceExpression? Expression = null
);

public sealed record VoiceProviderResult(
    byte[] AudioBytes,
    string ContentType = "audio/mpeg",
    TimeSpan? Duration = null
);

public interface IVoiceProvider
{
    Task<VoiceProviderResult> GenerateAudioAsync(
        VoiceProviderRequest request,
        CancellationToken ct = default);
}

public class VoiceTransientException : Exception
{
    public VoiceTransientException(string message) : base(message) { }
    public VoiceTransientException(string message, Exception inner) : base(message, inner) { }
}

public class VoiceNonTransientException : Exception
{
    public VoiceNonTransientException(string message) : base(message) { }
    public VoiceNonTransientException(string message, Exception inner) : base(message, inner) { }
}
