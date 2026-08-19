using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.LLM.Core;
using Infrastructure.LLM.Prompts;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.LLM;

public sealed class LLMService : ILLMService
{
    private readonly GeminiApiClient _geminiClient;

    public LLMService(GeminiApiClient geminiClient)
    {
        _geminiClient = geminiClient;
    }

    public async Task<string> GenerateRoleplayResponseAsync(
        Character character,
        IReadOnlyCollection<ChatMessage> history,
        string newUserMessage,
        CancellationToken ct = default)
    {
        var systemPrompt = RoleplayPrompts.BuildSystemPrompt(character);

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

        if (!recentHistory.Any(m => m.Content == newUserMessage && m.Role == MessageRole.User))
        {
            contentsList.Add(new
            {
                role = "user",
                parts = new[] { new { text = newUserMessage } }
            });
        }

        return await _geminiClient.GenerateTextAsync(
            systemPrompt: systemPrompt,
            contents: contentsList,
            temperature: 0.85,
            maxOutputTokens: 1000,
            ct: ct);
    }

    public async Task<GeneratedCharacterDto> GenerateCharacterProfileAsync(
        string idea,
        string? category = null,
        CancellationToken ct = default)
    {
        var userPrompt = $"Ý tưởng nhân vật: \"{idea}\"{(string.IsNullOrWhiteSpace(category) ? "" : $", Thể loại ưu tiên: {category}")}";

        var result = await _geminiClient.GenerateJsonAsync<GeneratedCharacterDto>(
            systemPrompt: CharacterGenerationPrompts.ProfileSystemPrompt,
            userPrompt: userPrompt,
            temperature: 0.75,
            ct: ct);

        if (result == null || string.IsNullOrWhiteSpace(result.Name))
        {
            throw new InvalidOperationException("Failed to generate valid character profile from AI.");
        }

        return result;
    }

    public async Task<List<string>> GenerateRandomIdeasAsync(
        int count = 3,
        CancellationToken ct = default)
    {
        var roles = new[]
        {
            "Nữ gia sư tiếng Anh tinh quái", "Nữ giám đốc công ty quyền lực", "Bà chủ tiệm mì ramen khuya",
            "Nữ ca sĩ thần tượng bí mật hẹn hò", "Tiểu thư sa sút làm hầu gái riêng", "Nữ cung thủ tinh linh hoang dã",
            "Thợ rèn ma thuật hậu đậu", "Nữ cảnh sát ngầm giả làm bạn gái", "Họa sĩ truyện tranh lập dị",
            "Nữ kiếm đạo sư coi bạn là đối thủ", "Bác sĩ thú y dịu dàng", "Nữ barista nghiện cà phê",
            "Cô bạn cùng phòng bất đắc dĩ", "Nữ đạo tặc chuyên trộm đồ của bạn", "Nữ hoàng băng giá bị phong ấn",
            "Cô bé bán hoa dạo bí ẩn", "Nữ thần hộ mệnh vụng về", "Nàng tiên cá trôi dạt vào bờ",
            "Nữ điệp viên hai mang cần bạn che chở", "Trưởng câu lạc bộ kịch nói kiêu kỳ", "Nữ y tá thực tập ngây thơ",
            "Nữ võ sĩ vô địch nhưng nhát gái", "Cô bạn thanh mai trúc mã làm YouTuber ẩm thực", "Nữ pháp sư thời gian bí ẩn"
        };

        var situations = new[]
        {
            "bị kẹt trong thang máy cùng bạn sau giờ làm", "nhận làm bạn gái hợp đồng để đối phó phụ huynh",
            "tình cờ ngồi chung bàn ở quán ăn khuya", "cùng bạn lén lút nuôi một chú mèo hoang",
            "phải chia đôi căn phòng trọ vì chủ nhà xếp nhầm", "được bạn cứu giúp khi quên ví tiền",
            "ngày nào cũng mang hộp cơm trưa tự làm sang cho bạn", "thách đấu bạn mỗi chiều ở sân tập",
            "lấy cớ hỏi bài để ở lại nhà bạn đến tối muộn", "lén nắm tay bạn mỗi khi đi qua chỗ đông người",
            "bắt bạn làm người nếm thử các món bánh mới", "tìm đến bạn để trút hết những áp lực giấu kín",
            "vô tình đọc trúng nhật ký bí mật của bạn", "ở nhờ nhà bạn để trốn cuộc hôn nhân sắp đặt",
            "ngày nào cũng xuất hiện trước cửa nhà rủ bạn đi dạo", "tự nhận là vị hôn thê từ kiếp trước của bạn"
        };

        var pickedRoles = roles.OrderBy(_ => Random.Shared.Next()).Take(3).ToArray();
        var pickedSituations = situations.OrderBy(_ => Random.Shared.Next()).Take(3).ToArray();

        var promptSeed = $"Gợi ý kết hợp 3 hình mẫu ngẫu nhiên: 1) {pickedRoles[0]} + {pickedSituations[0]}, 2) {pickedRoles[1]} + {pickedSituations[1]}, 3) {pickedRoles[2]} + {pickedSituations[2]}.";

        var systemPrompt = CharacterGenerationPrompts.BuildRandomIdeasSystemPrompt(count);
        var userPrompt = $"Hãy sáng tạo đúng {count} ý tưởng nhân vật Anime hoàn toàn mới, giàu cảm xúc và tương tác sâu sắc với người chơi. {promptSeed}. Mã ngẫu nhiên #{Random.Shared.Next(1000, 9999)}.";

        var list = await _geminiClient.GenerateJsonAsync<List<string>>(
            systemPrompt: systemPrompt,
            userPrompt: userPrompt,
            temperature: 1.15,
            ct: ct);

        if (list != null && list.Count > 0)
        {
            return list;
        }

        return new List<string>
        {
            $"{pickedRoles[0]} {pickedSituations[0]}.",
            $"{pickedRoles[1]} {pickedSituations[1]}.",
            $"{pickedRoles[2]} {pickedSituations[2]}."
        };
    }

    public async Task<List<string>> GenerateRoleplaySuggestionsAsync(
        Character character,
        IReadOnlyCollection<ChatMessage> history,
        CancellationToken ct = default)
    {
        var systemPrompt = SuggestionPrompts.BuildSuggestionSystemPrompt(character, history);
        var userPrompt = $"Hãy gợi ý 3 hướng phản hồi tiếp theo phù hợp với tình huống của {character.Name}.";

        var list = await _geminiClient.GenerateJsonAsync<List<string>>(
            systemPrompt: systemPrompt,
            userPrompt: userPrompt,
            temperature: 0.85,
            ct: ct);

        if (list != null && list.Count > 0)
        {
            return list;
        }

        return new List<string>
        {
            $"*Khẽ mỉm cười, nhìn thẳng vào mắt {character.Name} và nhẹ nhàng gật đầu*",
            $"*Lùi lại một bước, ánh mắt thăm dò vẻ mặt của {character.Name}*",
            $"*Im lặng quan sát, chờ xem phản ứng và diễn biến tiếp theo*"
        };
    }

    public async Task<GenerateAvatarResponse> GenerateAvatarAsync(
        GenerateAvatarRequest request,
        CancellationToken ct = default)
    {
        var systemPrompt = CharacterGenerationPrompts.BuildAvatarImagePrompt(
            request.Name,
            request.Title,
            request.Category,
            request.PersonalityPrompt,
            request.Idea);

        string cleanPrompt;
        try
        {
            var promptResult = await _geminiClient.GenerateTextAsync(
                systemPrompt: systemPrompt,
                contents: new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[] { new { text = "Generate the anime avatar image prompt tags now." } }
                    }
                },
                temperature: 0.7,
                maxOutputTokens: 120,
                ct: ct);

            cleanPrompt = (promptResult ?? "1girl, beautiful anime character portrait, masterpiece, best quality, Makoto Shinkai style, vibrant lighting, highly detailed face, 8k")
                .Replace("\n", ", ")
                .Trim();
        }
        catch
        {
            cleanPrompt = $"masterpiece, best quality, 2d anime illustration portrait of {request.Name ?? "anime character"}, {request.Title ?? "fantasy hero"}, beautiful expressive eyes, highly detailed face, vibrant colors, cinematic lighting, 8k, pixiv trending";
        }

        // Tối ưu hóa Prompt và sinh Seed ngẫu nhiên
        var finalImagePrompt = $"{cleanPrompt}, masterpiece, best quality, ultra-detailed, sharp focus, 8k";
        var randomSeed = Random.Shared.Next(1, 99999999);
        var imageUrl = $"https://image.pollinations.ai/prompt/{Uri.EscapeDataString(finalImagePrompt)}?width=512&height=512&nologo=true&seed={randomSeed}";

        // Tải ảnh trực tiếp từ Backend để mã hóa Base64 phục vụ render tức thì
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            var imageBytes = await http.GetByteArrayAsync(imageUrl, ct);
            if (imageBytes != null && imageBytes.Length > 1000)
            {
                var base64 = Convert.ToBase64String(imageBytes);
                var dataUrl = $"data:image/jpeg;base64,{base64}";
                return new GenerateAvatarResponse(dataUrl, cleanPrompt);
            }
        }
        catch
        {
            // Fallback trả về direct URL nếu kết nối tải ảnh mất nhiều thời gian
        }

        return new GenerateAvatarResponse(imageUrl, cleanPrompt);
    }

    public async Task<GenerateAvatarResponse> GenerateSceneImageAsync(
        GenerateSceneImageRequest request,
        CancellationToken ct = default)
    {
        var scenePromptBuilder = $"""
            You are a World-Class Visual Novel & Cinematic Key Moment Illustrator.
            
            Character:
            - Name: {request.CharacterName ?? "Character"}
            - Title: {request.CharacterTitle ?? "Role"}
            - Personality / Lore: {request.CharacterPersonality ?? "Fascinating character"}
            
            Current Roleplay Dialogue & Interaction Moment:
            - Player action/dialogue: "{request.UserMessageContent ?? "Interacting together"}"
            - Character action/dialogue: "{request.MessageContent}"
            
            TASK:
            Translate this exact interaction scene into a focused, highly emotional English image prompt (35 - 50 comma-separated tags):
            1. Depict the specific physical interaction (e.g. leaning in, gripping player's sleeve, intense eye contact, blush, hugging, sitting together, emotional expression).
            2. Match character's visual traits (hair, eyes, clothing).
            3. Use dynamic visual novel / cinematic perspective (close-up, over-the-shoulder, point of view from player, dramatic soft lighting, masterpiece, best quality, depth of field, 8k).
            
            Output ONLY the raw comma-separated English tags.
            """;

        string cleanPrompt;
        try
        {
            var promptResult = await _geminiClient.GenerateTextAsync(
                "You are an expert visual novel and cinematic scene prompt generator. Output only comma-separated English tags.",
                new[] { scenePromptBuilder },
                temperature: 0.7,
                maxOutputTokens: 250,
                ct: ct);

            cleanPrompt = (promptResult ?? "1girl, close up, emotional eye contact, gripping sleeve, dramatic lighting, masterpiece, best quality, 8k")
                .Replace("\n", ", ")
                .Trim();
        }
        catch
        {
            cleanPrompt = $"masterpiece, best quality, close up interaction portrait of {request.CharacterName ?? "anime character"}, emotional eye contact, blushing, highly detailed, dramatic lighting, 8k";
        }

        var finalImagePrompt = $"{cleanPrompt}, masterpiece, best quality, scenic, detailed background, cinematic lighting, ultra-detailed, 8k";

        // 1. Thử tạo ảnh bằng Google Imagen 3 chính chủ (Tỉ lệ 16:9 điện ảnh)
        try
        {
            var imagenDataUrl = await _geminiClient.GenerateImageWithImagenAsync(finalImagePrompt, "16:9", ct);
            if (!string.IsNullOrEmpty(imagenDataUrl))
            {
                return new GenerateAvatarResponse(imagenDataUrl, cleanPrompt);
            }
        }
        catch
        {
            // Fallback
        }

        // 2. Fallback sang hệ thống render dự phòng
        var randomSeed = Random.Shared.Next(1, 99999999);
        var imageUrl = $"https://image.pollinations.ai/prompt/{Uri.EscapeDataString(finalImagePrompt)}?width=896&height=512&nologo=true&seed={randomSeed}";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            var imageBytes = await http.GetByteArrayAsync(imageUrl, ct);
            if (imageBytes != null && imageBytes.Length > 1000)
            {
                var base64 = Convert.ToBase64String(imageBytes);
                var dataUrl = $"data:image/jpeg;base64,{base64}";
                return new GenerateAvatarResponse(dataUrl, cleanPrompt);
            }
        }
        catch
        {
            // Fallback
        }

        return new GenerateAvatarResponse(imageUrl, cleanPrompt);
    }
}
