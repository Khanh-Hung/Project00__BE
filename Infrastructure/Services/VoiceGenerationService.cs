using Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class VoiceGenerationService : IVoiceGenerationService
{
    private readonly HttpClient _httpClient;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<VoiceGenerationService> _logger;

    public VoiceGenerationService(
        HttpClient httpClient,
        IWebHostEnvironment env,
        ILogger<VoiceGenerationService> logger)
    {
        _httpClient = httpClient;
        _env = env;
        _logger = logger;
    }

    public async Task<VoiceGenerationResult> GenerateVoiceAsync(
        VoiceGenerationRequest request,
        CancellationToken ct = default)
    {
        var text = request.CleanedText;
        var voiceId = request.VoiceId;
        var lang = request.Language ?? "vi-VN";

        // Try free Google TTS API / Edge TTS compatible endpoint
        var encodedText = Uri.EscapeDataString(text);
        var langCode = lang.Split('-')[0]; // "vi" or "en"
        var ttsUrl = $"https://translate.google.com/translate_tts?ie=UTF-8&q={encodedText}&tl={langCode}&client=tw-ob";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ttsUrl);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            var response = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes != null && bytes.Length > 100)
                {
                    var webRoot = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                    var audioDir = Path.Combine(webRoot, "uploads", "audio");
                    if (!Directory.Exists(audioDir))
                    {
                        Directory.CreateDirectory(audioDir);
                    }

                    var fileName = $"{Guid.NewGuid():N}.mp3";
                    var fullPath = Path.Combine(audioDir, fileName);
                    await File.WriteAllBytesAsync(fullPath, bytes, ct);

                    var audioUrl = $"/uploads/audio/{fileName}";
                    _logger.LogInformation("Voice generated and saved to {AudioUrl}", audioUrl);
                    return new VoiceGenerationResult(audioUrl, "audio/mpeg");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download TTS audio. Returning fallback mock result.");
        }

        // Fallback placeholder/direct url
        return new VoiceGenerationResult(ttsUrl, "audio/mpeg");
    }
}
