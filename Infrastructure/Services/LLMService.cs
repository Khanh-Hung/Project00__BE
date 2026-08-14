using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using System.Text.Json;

namespace Infrastructure.Services;

public class LLMService : ILLMService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LLMService> _logger;

    public LLMService(HttpClient httpClient, IConfiguration configuration, ILogger<LLMService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> GenerateRoleplayResponseAsync(
        Character character,
        IReadOnlyCollection<ChatMessage> history,
        string newUserMessage,
        CancellationToken ct = default)
    {
        var baseUrl = _configuration["AI:BaseUrl"];
        var model = _configuration["AI:Model"];
        var apiKey = _configuration["AI:ApiKey"]
            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("AI:BaseUrl is not configured in appsettings.json.");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("AI:Model is not configured in appsettings.json.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("AI:ApiKey is not configured. Please set your API Key in 'AI:ApiKey' inside appsettings.Development.json.");
        }

        var systemPrompt = $"""
            You are playing the role of {character.Name}.
            Title/Role: {character.Category} - {character.Title}
            
            Personality & Backstory:
            {character.PersonalityPrompt}
            
            Rules:
            1. Always stay in character as {character.Name}. Never mention that you are an AI.
            2. Express actions using *asterisks* (e.g., *smiles gently* or *thinks carefully*).
            3. Match the user's language.
            """;

        var messagesList = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };

        var recentHistory = history.TakeLast(10);
        foreach (var msg in recentHistory)
        {
            var roleStr = msg.Role switch
            {
                MessageRole.User => "user",
                MessageRole.Assistant => "assistant",
                _ => "system"
            };
            messagesList.Add(new { role = roleStr, content = msg.Content });
        }

        var requestPayload = new
        {
            model = model,
            messages = messagesList,
            temperature = 0.8,
            max_tokens = 500
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(requestPayload);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var jsonResult = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var content = jsonResult
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return content ?? string.Empty;
    }
}
