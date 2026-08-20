using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class EmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmbeddingService> _logger;
    private readonly string? _apiKey;

    public EmbeddingService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<EmbeddingService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
        _apiKey = configuration["Gemini:ApiKey"] ?? configuration["AiProviders:Gemini:ApiKey"] ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<float>();
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return GenerateDeterministicLocalEmbedding(text);
        }

        try
        {
            var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/text-embedding-004:embedContent?key={_apiKey}";
            var requestBody = new
            {
                model = "models/text-embedding-004",
                content = new
                {
                    parts = new[] { new { text } }
                }
            };

            var response = await _httpClient.PostAsJsonAsync(endpoint, requestBody, ct);
            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("embedding", out var emb) &&
                    emb.TryGetProperty("values", out var values))
                {
                    var result = new List<float>();
                    foreach (var val in values.EnumerateArray())
                    {
                        result.Add((float)val.GetDouble());
                    }
                    return result.ToArray();
                }
            }
            else
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Gemini Embedding API returned status {StatusCode}: {Error}. Falling back to local embedding.", response.StatusCode, err);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to generate remote embedding with Gemini. Using local fallback.");
        }

        return GenerateDeterministicLocalEmbedding(text);
    }

    public async Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new List<float[]>();
        foreach (var text in texts)
        {
            var vector = await GenerateEmbeddingAsync(text, ct);
            result.Add(vector);
        }
        return result;
    }

    /// <summary>
    /// Fast, deterministic multilingual n-gram vectorizer (256 dimensions) for zero-dependency local embeddings.
    /// </summary>
    public static float[] GenerateDeterministicLocalEmbedding(string text, int dimensions = 256)
    {
        var vector = new float[dimensions];
        if (string.IsNullOrWhiteSpace(text)) return vector;

        var normalized = text.Trim().ToLowerInvariant();
        var words = normalized.Split(new[] { ' ', ',', '.', '!', '?', ';', ':', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(word));
            for (int i = 0; i < hash.Length && i < dimensions; i++)
            {
                var index = (hash[i] + i) % dimensions;
                vector[index] += 1.0f;
            }
        }

        // Normalize vector to unit length
        double norm = 0.0;
        for (int i = 0; i < dimensions; i++)
        {
            norm += vector[i] * vector[i];
        }

        if (norm > 0)
        {
            var sqrtNorm = (float)Math.Sqrt(norm);
            for (int i = 0; i < dimensions; i++)
            {
                vector[i] /= sqrtNorm;
            }
        }

        return vector;
    }
}
