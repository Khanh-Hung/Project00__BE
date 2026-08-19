using Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Storage;

public sealed class LocalStorageService : IStorageService
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<LocalStorageService> _logger;

    public LocalStorageService(IWebHostEnvironment env, ILogger<LocalStorageService> logger)
    {
        _env = env;
        _logger = logger;
    }

    public async Task<string> SaveImageAsync(byte[] imageBytes, string fileName, string contentType = "image/jpeg", CancellationToken ct = default)
    {
        var webRoot = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        var uploadsDir = Path.Combine(webRoot, "uploads");

        if (!Directory.Exists(uploadsDir))
        {
            Directory.CreateDirectory(uploadsDir);
        }

        var safeFileName = $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}";
        var fullPath = Path.Combine(uploadsDir, safeFileName);

        await File.WriteAllBytesAsync(fullPath, imageBytes, ct);
        _logger.LogInformation("Saved image locally to: {Path}", fullPath);

        return $"/uploads/{safeFileName}";
    }

    public async Task<string> SaveBase64ImageAsync(string base64Data, string fileName, CancellationToken ct = default)
    {
        var cleanBase64 = base64Data;
        if (base64Data.Contains(","))
        {
            cleanBase64 = base64Data.Split(',')[1];
        }

        var bytes = Convert.FromBase64String(cleanBase64);
        return await SaveImageAsync(bytes, fileName, "image/jpeg", ct);
    }

    public Task<bool> DeleteFileAsync(string fileUrl, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fileUrl) || !fileUrl.StartsWith("/uploads/"))
            {
                return Task.FromResult(false);
            }

            var webRoot = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            var relativePath = fileUrl.TrimStart('/');
            var fullPath = Path.Combine(webRoot, relativePath);

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                return Task.FromResult(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete file: {Url}", fileUrl);
        }

        return Task.FromResult(false);
    }
}
