using Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Storage;

public sealed class LocalVoiceStorage : IVoiceStorage
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<LocalVoiceStorage> _logger;

    public LocalVoiceStorage(IWebHostEnvironment env, ILogger<LocalVoiceStorage> logger)
    {
        _env = env;
        _logger = logger;
    }

    public async Task<string> SaveAudioAsync(
        byte[] audioBytes,
        string fileName,
        string contentType = "audio/mpeg",
        CancellationToken ct = default)
    {
        var webRoot = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        var audioDir = Path.Combine(webRoot, "uploads", "audio");

        if (!Directory.Exists(audioDir))
        {
            Directory.CreateDirectory(audioDir);
        }

        var safeFileName = $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}";
        var fullPath = Path.Combine(audioDir, safeFileName);

        await File.WriteAllBytesAsync(fullPath, audioBytes, ct);
        _logger.LogInformation("Saved audio locally to: {Path}", fullPath);

        return $"/uploads/audio/{safeFileName}";
    }

    public Task<bool> DeleteAudioAsync(string audioUrl, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(audioUrl) || !audioUrl.StartsWith("/uploads/audio/"))
            {
                return Task.FromResult(false);
            }

            var webRoot = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            var relativePath = audioUrl.TrimStart('/');
            var fullPath = Path.Combine(webRoot, relativePath);

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                _logger.LogInformation("Deleted audio file: {Path}", fullPath);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete audio file at: {AudioUrl}", audioUrl);
            return Task.FromResult(false);
        }
    }
}
