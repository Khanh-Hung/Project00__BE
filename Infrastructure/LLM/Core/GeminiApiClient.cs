using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;

namespace Infrastructure.LLM.Core;

public sealed class GeminiApiClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GeminiApiClient> _logger;

    public GeminiApiClient(HttpClient httpClient, IConfiguration configuration, ILogger<GeminiApiClient> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string GetApiKey()
    {
        var apiKey = _configuration["AI:ApiKey"]
            ?? _configuration["Gemini:ApiKey"]
            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("AI:ApiKey is not configured. Please set your API Key in 'AI:ApiKey' inside appsettings.Development.json.");
        }

        return apiKey;
    }

    private List<string> GetCandidateModels()
    {
        var models = new List<string>();
        var configured = _configuration["AI:Model"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var clean = configured.StartsWith("models/") ? configured.Substring(7) : configured;
            models.Add(clean);
        }

        models.AddRange(new[] { "gemini-3.1-flash-lite", "gemini-flash-latest", "gemini-3.1-flash-lite-preview" });
        return models.Distinct().ToList();
    }

    public async Task<string> GenerateTextAsync(
        string systemPrompt,
        IEnumerable<object> contents,
        double temperature = 0.85,
        int maxOutputTokens = 1000,
        CancellationToken ct = default)
    {
        var apiKey = GetApiKey();
        var models = GetCandidateModels();

        var requestPayload = new
        {
            systemInstruction = new
            {
                parts = new[] { new { text = systemPrompt } }
            },
            contents = contents,
            generationConfig = new
            {
                temperature = temperature,
                maxOutputTokens = maxOutputTokens
            }
        };

        Exception? lastException = null;

        foreach (var modelName in models)
        {
            try
            {
                var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={apiKey}";
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Content = JsonContent.Create(requestPayload);

                var response = await _httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var errBody = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Model {ModelName} failed with status {StatusCode}: {ErrorBody}", modelName, response.StatusCode, errBody);
                    continue;
                }

                var jsonResult = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                if (jsonResult.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                {
                    var parts = candidates[0].GetProperty("content").GetProperty("parts");
                    if (parts.GetArrayLength() > 0)
                    {
                        var content = parts[0].GetProperty("text").GetString();
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            return content;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                _logger.LogWarning(ex, "Exception when calling Gemini model {ModelName}", modelName);
            }
        }

        if (lastException != null)
        {
            throw lastException;
        }

        throw new InvalidOperationException("Failed to receive response from Gemini AI models. Please try again.");
    }

    public async IAsyncEnumerable<string> StreamTextAsync(
        string systemPrompt,
        IEnumerable<object> contents,
        double temperature = 0.85,
        int maxOutputTokens = 1000,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var apiKey = GetApiKey();
        var models = GetCandidateModels();

        var requestPayload = new
        {
            systemInstruction = new
            {
                parts = new[] { new { text = systemPrompt } }
            },
            contents = contents,
            generationConfig = new
            {
                temperature = temperature,
                maxOutputTokens = maxOutputTokens
            }
        };

        foreach (var modelName in models)
        {
            var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:streamGenerateContent?alt=sse&key={apiKey}";
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = JsonContent.Create(requestPayload);

            HttpResponseMessage? response = null;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Model {ModelName} failed to start streaming", modelName);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Model {ModelName} streaming failed with status {StatusCode}: {ErrorBody}", modelName, response.StatusCode, errBody);
                continue;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new System.IO.StreamReader(stream);

            string? line;
            while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) != null)
            {
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ")) continue;

                var jsonChunk = line.Substring(6).Trim();
                if (string.IsNullOrWhiteSpace(jsonChunk) || jsonChunk == "[DONE]") continue;

                string? chunkText = null;
                try
                {
                    using var doc = JsonDocument.Parse(jsonChunk);
                    if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                    {
                        var parts = candidates[0].GetProperty("content").GetProperty("parts");
                        if (parts.GetArrayLength() > 0)
                        {
                            chunkText = parts[0].GetProperty("text").GetString();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex, "Failed to parse streaming JSON chunk: {Chunk}", jsonChunk);
                }

                if (!string.IsNullOrEmpty(chunkText))
                {
                    yield return chunkText;
                }
            }

            yield break;
        }
    }

    public async Task<T?> GenerateJsonAsync<T>(
        string systemPrompt,
        string userPrompt,
        double temperature = 0.75,
        CancellationToken ct = default) where T : class
    {
        var apiKey = GetApiKey();
        var models = GetCandidateModels();

        var requestPayload = new
        {
            systemInstruction = new
            {
                parts = new[] { new { text = systemPrompt } }
            },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = userPrompt } }
                }
            },
            generationConfig = new
            {
                temperature = temperature,
                responseMimeType = "application/json"
            }
        };

        foreach (var modelName in models)
        {
            try
            {
                var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={apiKey}";
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Content = JsonContent.Create(requestPayload);

                var response = await _httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var jsonResult = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                if (jsonResult.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                {
                    var parts = candidates[0].GetProperty("content").GetProperty("parts");
                    if (parts.GetArrayLength() > 0)
                    {
                        var rawText = parts[0].GetProperty("text").GetString();
                        if (!string.IsNullOrWhiteSpace(rawText))
                        {
                            var cleanJson = CleanJsonString(rawText);
                            var result = JsonSerializer.Deserialize<T>(cleanJson, new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            });

                            if (result != null)
                            {
                                return result;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed generating JSON with model {ModelName}", modelName);
            }
        }

        return null;
    }

    private static string CleanJsonString(string raw)
    {
        var clean = raw.Trim();
        if (clean.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            clean = clean.Substring(7);
        if (clean.StartsWith("```"))
            clean = clean.Substring(3);
        if (clean.EndsWith("```"))
            clean = clean.Substring(0, clean.Length - 3);
        return clean.Trim();
    }

    public async Task<string?> GenerateImageWithImagenAsync(
        string prompt,
        string aspectRatio = "16:9",
        CancellationToken ct = default)
    {
        var apiKey = GetApiKey();
        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/imagen-3.0-generate-002:predict?key={apiKey}";

        var payload = new
        {
            instances = new[]
            {
                new { prompt = prompt }
            },
            parameters = new
            {
                sampleCount = 1,
                aspectRatio = aspectRatio,
                outputMimeType = "image/jpeg"
            }
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = JsonContent.Create(payload);
            using var response = await _httpClient.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (doc.RootElement.TryGetProperty("predictions", out var predictions) &&
                    predictions.GetArrayLength() > 0)
                {
                    var first = predictions[0];
                    if (first.TryGetProperty("bytesBase64Encoded", out var b64Prop))
                    {
                        var b64 = b64Prop.GetString();
                        if (!string.IsNullOrEmpty(b64))
                        {
                            return $"data:image/jpeg;base64,{b64}";
                        }
                    }
                }
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[Imagen3] Imagen request returned {StatusCode}: {Error}", response.StatusCode, error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Imagen3] Exception calling Google Imagen 3 API");
        }

        return null;
    }
}
