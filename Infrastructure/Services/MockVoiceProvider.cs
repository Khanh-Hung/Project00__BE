using Application.Interfaces;

namespace Infrastructure.Services;

/// <summary>
/// Deterministic mock voice provider for local test execution ($0 cost).
/// Generates deterministic mock MP3 audio bytes without calling external APIs.
/// </summary>
public sealed class MockVoiceProvider : IVoiceProvider
{
    private readonly Func<VoiceProviderRequest, CancellationToken, Task<VoiceProviderResult>>? _customHandler;
    private int _callCount = 0;

    public int CallCount => _callCount;

    public MockVoiceProvider(Func<VoiceProviderRequest, CancellationToken, Task<VoiceProviderResult>>? customHandler = null)
    {
        _customHandler = customHandler;
    }

    public async Task<VoiceProviderResult> GenerateAudioAsync(VoiceProviderRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _callCount);

        if (_customHandler != null)
        {
            return await _customHandler(request, ct);
        }

        // Generate deterministic mock MP3 bytes (valid MPEG header frame)
        var mockBytes = new byte[]
        {
            0xFF, 0xFB, 0x90, 0x64, // MPEG-1 Audio Layer III sync header
            0x00, 0x00, 0x00, 0x00,
            0x54, 0x41, 0x47, 0x00, // ID3 tag marker
            0x4D, 0x6F, 0x63, 0x6B, // "Mock"
            0x56, 0x6F, 0x69, 0x63, // "Voic"
            0x65, 0x20, 0x41, 0x75, // "e Au"
            0x64, 0x69, 0x6F, 0x00  // "dio\0"
        };

        var estimatedDuration = TimeSpan.FromSeconds(Math.Max(1.0, (request.CleanedText?.Length ?? 10) * 0.08));

        return new VoiceProviderResult(
            AudioBytes: mockBytes,
            ContentType: "audio/mpeg",
            Duration: estimatedDuration
        );
    }
}
