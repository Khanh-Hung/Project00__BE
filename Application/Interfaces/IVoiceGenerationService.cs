namespace Application.Interfaces;

public sealed record VoiceGenerationResult(
    string AudioUrl,
    string AudioFormat = "audio/mpeg",
    TimeSpan? Duration = null
)
{
    public VoiceGenerationResult(string audioUrl, string audioFormat, int durationSeconds)
        : this(audioUrl, audioFormat, TimeSpan.FromSeconds(durationSeconds)) { }
}

public interface IVoiceGenerationService
{
    Task<VoiceGenerationResult> GenerateVoiceAsync(
        VoiceProviderRequest request,
        CancellationToken ct = default);
}
