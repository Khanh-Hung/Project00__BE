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
            You are a master interactive roleplayer fully embodying the character: {character.Name}.
            Role & Category: {character.Category} - {character.Title}
            
            Character Personality, Lore & Backstory:
            {character.PersonalityPrompt}
            
            PSYCHOLOGICAL 3-LAYER ROLEPLAY GUIDELINES:
            Do not provide dry, blunt, or robotic responses. Make your character feel genuinely alive with deep emotional nuance and psychological progression:
            
            1. 【Inner Thoughts / Độc thoại nội tâm】:
               Show the character's internal reflections, secret doubts, emotional reactions, or strategic thoughts before/during speaking using the format:
               💭 *(suy nghĩ thầm kín trong đầu...)*
            
            2. 【Actions & Micro-Expressions / Cử chỉ & Biểu cảm】:
               Depict subtle physical reactions, breathing, glances, posture, body language, and actions wrapped in *asterisks*, e.g. *ánh mắt khẽ lay động, ngón tay bất giác siết nhẹ*.
            
            3. 【Dynamic Spoken Dialogue / Lời thoại sống động】:
               Speak with natural pacing, personality-driven tone, pauses, and authentic voice.
            
            CORE RULES:
            - Always remain 100% in character as {character.Name}. NEVER break character or mention AI/LLM.
            - Respond in natural, vivid, and evocative Vietnamese (or the language used by user).
            - Balance thoughts, actions, and speech to create an immersive story.
            """;

        var contentsList = new List<object>();

        var recentHistory = history.TakeLast(10);
        foreach (var msg in recentHistory)
        {
            var roleStr = msg.Role switch
            {
                MessageRole.User => "user",
                MessageRole.Assistant => "model",
                _ => "user"
            };
            contentsList.Add(new
            {
                role = roleStr,
                parts = new[] { new { text = msg.Content } }
            });
        }

        // Add current new user message if not already in history
        if (!recentHistory.Any(m => m.Content == newUserMessage && m.Role == MessageRole.User))
        {
            contentsList.Add(new
            {
                role = "user",
                parts = new[] { new { text = newUserMessage } }
            });
        }

        var candidateModels = new List<string>();
        if (!string.IsNullOrWhiteSpace(model))
        {
            var configured = model.StartsWith("models/") ? model.Substring(7) : model;
            candidateModels.Add(configured);
        }
        candidateModels.AddRange(new[] { "gemini-3.1-flash-lite", "gemini-flash-latest", "gemini-3.1-flash-lite-preview" });
        candidateModels = candidateModels.Distinct().ToList();

        var requestPayload = new
        {
            systemInstruction = new
            {
                parts = new[] { new { text = systemPrompt } }
            },
            contents = contentsList,
            generationConfig = new
            {
                temperature = 0.85,
                maxOutputTokens = 1000
            }
        };

        Exception? lastException = null;

        foreach (var modelName in candidateModels)
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
                _logger.LogWarning(ex, "Exception when calling model {ModelName}", modelName);
            }
        }

        if (lastException != null)
        {
            throw lastException;
        }

        throw new InvalidOperationException("Không thể nhận phản hồi từ các mô hình AI. Vui lòng thử lại sau giây lát.");
    }

    public async Task<Application.DTOs.GeneratedCharacterDto> GenerateCharacterProfileAsync(
        string idea,
        string? category = null,
        CancellationToken ct = default)
    {
        var model = _configuration["AI:Model"];
        var apiKey = _configuration["AI:ApiKey"]
            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("AI:ApiKey is not configured.");
        }

        var systemPrompt = """
            Bạn là chuyên gia cố vấn sáng tạo nhân vật nhập vai Anime/Game/Fantasy hàng đầu.
            Nhiệm vụ của bạn là dựa vào ý tưởng người dùng cung cấp để sáng tạo một nhân vật AI nhập vai độc đáo, cuốn hút và sống động.

            Yêu cầu bắt buộc: Phải trả về DUY NHẤT một chuỗi JSON hợp lệ (không kèm giải thích ngoài JSON) theo đúng cấu trúc sau:
            {
              "name": "Tên nhân vật (ví dụ: Elena Dạ Nguyệt, Kaelen, Lyra...)",
              "title": "Danh hiệu / Vai trò ngắn gọn (ví dụ: Nữ Đại Pháp Sư Băng Giá, Vệ Sĩ Hoàng Gia...)",
              "category": "Chọn 1 trong các thể loại sau: Companion, Anime, Fantasy, RPG, Assistant, Mentor",
              "personalityPrompt": "Viết một đoạn văn tiểu thuyết nhập vai lôi cuốn (120-220 từ) giới thiệu: (1) Nhân vật này là ai; (2) VAI TRÒ CỦA BẠN (người chơi là ai: ví dụ người bạn thanh mai trúc mã / lữ khách tình cờ gặp gỡ / chủ nhân / ân nhân cứu mạng...); (3) MỐI QUAN HỆ giữa hai người và cách nhân vật đối xử, xưng hô với bạn. Bắt đầu bằng '[Tên nhân vật] là...'. Tuyệt đối không dùng từ máy móc như 'Quy tắc ứng xử:', 'người dùng' mà hãy lồng ghép tự nhiên vào bối cảnh.",
              "greeting": "Lời chào mở đầu sống động kèm cử chỉ hành động trong dấu *sao* hướng về phía bạn (ví dụ: *khẽ nâng cây trượng nhìn bạn qua làn sương* Kẻ lạ mặt, ngươi tìm kiếm điều gì?)",
              "tags": ["3-5 thẻ từ khóa đặc trưng ngắn gọn như: Băng tuyết, Tsundere, Ma pháp, Bí ẩn"]
            }
            """;

        var userPrompt = $"Ý tưởng nhân vật: \"{idea}\"{(string.IsNullOrWhiteSpace(category) ? "" : $", Thể loại ưu tiên: {category}")}";

        var candidateModels = new List<string>();
        if (!string.IsNullOrWhiteSpace(model))
        {
            var configured = model.StartsWith("models/") ? model.Substring(7) : model;
            candidateModels.Add(configured);
        }
        candidateModels.AddRange(new[] { "gemini-3.1-flash-lite", "gemini-flash-latest", "gemini-3.1-flash-lite-preview" });
        candidateModels = candidateModels.Distinct().ToList();

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
                temperature = 0.75,
                responseMimeType = "application/json"
            }
        };

        foreach (var modelName in candidateModels)
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
                        var content = parts[0].GetProperty("text").GetString();
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            var cleanJson = content.Trim();
                            if (cleanJson.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
                                cleanJson = cleanJson.Substring(7);
                            if (cleanJson.StartsWith("```"))
                                cleanJson = cleanJson.Substring(3);
                            if (cleanJson.EndsWith("```"))
                                cleanJson = cleanJson.Substring(0, cleanJson.Length - 3);
                            cleanJson = cleanJson.Trim();

                            var parsed = JsonSerializer.Deserialize<Application.DTOs.GeneratedCharacterDto>(cleanJson, new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            });

                            if (parsed != null && !string.IsNullOrWhiteSpace(parsed.Name))
                            {
                                return parsed;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed generating character profile with model {ModelName}", modelName);
            }
        }

        throw new InvalidOperationException("Không thể tự động sinh nhân vật bằng AI lúc này. Vui lòng thử lại sau!");
    }

    public async Task<List<string>> GenerateRandomIdeasAsync(int count = 4, CancellationToken ct = default)
    {
        var model = _configuration["AI:Model"];
        var apiKey = _configuration["AI:ApiKey"]
            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("AI:ApiKey is not configured.");
        }

        var systemPrompt = $"""
            Bạn là chuyên gia sáng tạo kịch bản Anime, Game và Tiểu thuyết nhập vai.
            Nhiệm vụ của bạn là nghĩ ra {count} ý tưởng nhân vật nhập vai ngắn gọn (khoảng 8-15 từ mỗi ý tưởng) cực kỳ độc đáo, bất ngờ, cuốn hút và đa dạng thể loại (Cyberpunk, Anime, Kỳ ảo, Đời thường, Yandere, Hài hước, v.v.).
            
            Yêu cầu: Trả về DUY NHẤT một mảng JSON các chuỗi (string array) bằng tiếng Việt theo định dạng:
            ["Ý tưởng 1", "Ý tưởng 2", "Ý tưởng 3", "Ý tưởng 4"]
            """;

        var candidateModels = new List<string>();
        if (!string.IsNullOrWhiteSpace(model))
        {
            var configured = model.StartsWith("models/") ? model.Substring(7) : model;
            candidateModels.Add(configured);
        }
        candidateModels.AddRange(new[] { "gemini-3.1-flash-lite", "gemini-flash-latest", "gemini-3.1-flash-lite-preview" });
        candidateModels = candidateModels.Distinct().ToList();

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
                    parts = new[] { new { text = $"Hãy sinh ngẫu nhiên {count} ý tưởng nhân vật nhập vai mới lạ khác nhau." } }
                }
            },
            generationConfig = new
            {
                temperature = 0.95,
                responseMimeType = "application/json"
            }
        };

        foreach (var modelName in candidateModels)
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
                        var content = parts[0].GetProperty("text").GetString();
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            var cleanJson = content.Trim();
                            if (cleanJson.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
                                cleanJson = cleanJson.Substring(7);
                            if (cleanJson.StartsWith("```"))
                                cleanJson = cleanJson.Substring(3);
                            if (cleanJson.EndsWith("```"))
                                cleanJson = cleanJson.Substring(0, cleanJson.Length - 3);
                            cleanJson = cleanJson.Trim();

                            var list = JsonSerializer.Deserialize<List<string>>(cleanJson, new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            });

                            if (list != null && list.Count > 0)
                            {
                                return list;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed generating random ideas with model {ModelName}", modelName);
            }
        }

        return new List<string>
        {
            "Nữ pháp sư hệ Băng sống ẩn dật ngoài lạnh trong ấm",
            "Bạn học cũ thanh mai trúc mã tinh nghịch hay trêu chọc",
            "Hiệp sĩ bóng đêm mang lời nguyền cô độc",
            "Tiểu thư ma cà rồng quý tộc kiêu kỳ thích ăn ngọt"
        };
    }
}
