using Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Application service implementing IVoiceGenerationService by coordinating IVoiceProvider and IVoiceStorage.
/// </summary>
public sealed class VoiceGenerationService : IVoiceGenerationService
{
    private readonly IVoiceProvider _voiceProvider;
    private readonly IVoiceStorage _voiceStorage;
    private readonly ILogger<VoiceGenerationService> _logger;

    public VoiceGenerationService(
        IVoiceProvider voiceProvider,
        IVoiceStorage voiceStorage,
        ILogger<VoiceGenerationService> logger)
    {
        _voiceProvider = voiceProvider;
        _voiceStorage = voiceStorage;
        _logger = logger;
    }

    public async Task<VoiceGenerationResult> GenerateVoiceAsync(
        VoiceProviderRequest request,
        CancellationToken ct = default)
    {
        var providerResult = await _voiceProvider.GenerateAudioAsync(request, ct);

        var fileName = $"{Guid.NewGuid():N}.mp3";
        var audioUrl = await _voiceStorage.SaveAudioAsync(
            providerResult.AudioBytes,
            fileName,
            providerResult.ContentType,
            ct);

        _logger.LogInformation("Voice generation succeeded: AudioUrl={AudioUrl}", audioUrl);

        return new VoiceGenerationResult(
            AudioUrl: audioUrl,
            AudioFormat: providerResult.ContentType,
            Duration: providerResult.Duration
        );
    }
}
