namespace Application.Interfaces;

public interface IVoiceGenerationService
{
    Task<VoiceGenerationResult> GenerateVoiceAsync(
        VoiceGenerationRequest request,
        CancellationToken ct = default);
}
