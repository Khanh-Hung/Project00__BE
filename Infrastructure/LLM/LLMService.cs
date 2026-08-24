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
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace Infrastructure.LLM;

public sealed class LLMService : ILLMService
{
    private readonly GeminiApiClient _geminiClient;
    private readonly IImageGenerationService _imageService;
    private readonly IPromptCompiler _promptCompiler;

    public LLMService(
        GeminiApiClient geminiClient,
        IImageGenerationService imageService,
        IPromptCompiler promptCompiler)
    {
        _geminiClient = geminiClient;
        _imageService = imageService;
        _promptCompiler = promptCompiler;
    }

    private record RoleplayEventJsonDto(
        string? Key,
        string? Context
    );

    private record RoleplayAiJsonDto(
        string Reply,
        string? Mood,
        int? MoodIntensity,
        int? AffectionDelta,
        RoleplayEventJsonDto? Event,
        bool? HasWalkedOut,
        string? WalkOutReason
    );

    public async Task<RoleplayTurnResult> GenerateRoleplayTurnAsync(
        Application.Common.RoleplayContext context,
        CancellationToken ct = default)
    {
        var systemPrompt = _promptCompiler.CompileSystemPrompt(context);
        var contentsList = _promptCompiler.CompileConversationContents(context);

        var rawResponse = await _geminiClient.GenerateTextAsync(
            systemPrompt: systemPrompt,
            contents: contentsList,
            temperature: 0.85,
            maxOutputTokens: 1000,
            ct: ct);

        return Application.Common.StructuredTurnParser.Parse(rawResponse);
    }

    public async IAsyncEnumerable<string> GenerateRoleplayTurnStreamAsync(
        Application.Common.RoleplayContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var systemPrompt = _promptCompiler.CompileSystemPrompt(context);
        var contentsList = _promptCompiler.CompileConversationContents(context);

        await foreach (var chunk in _geminiClient.StreamTextAsync(systemPrompt, contentsList, ct: ct))
        {
            yield return chunk;
        }
    }

    public async Task<RoleplayTurnResult> GenerateRoleplayTurnAsync(
        Character character,
        IReadOnlyCollection<ChatMessage> history,
        string newUserMessage,
        CharacterRelationship? relationship = null,
        IReadOnlyCollection<CharacterMemory>? memories = null,
        CancellationToken ct = default)
    {
        var dummySession = new ChatSession(character.Id, relationship?.UserId, "Session");
        var context = new Application.Common.RoleplayContext(
            character,
            relationship,
            memories?.ToList() ?? new List<CharacterMemory>(),
            history?.ToList() ?? new List<ChatMessage>(),
            newUserMessage,
            dummySession
        );

        return await GenerateRoleplayTurnAsync(context, ct);
    }

    public async Task<string> GenerateRoleplayResponseAsync(
        Character character,
        IReadOnlyCollection<ChatMessage> history,
        string newUserMessage,
        CharacterRelationship? relationship = null,
        CancellationToken ct = default)
    {
        var turn = await GenerateRoleplayTurnAsync(character, history, newUserMessage, relationship, memories: null, ct: ct);
        return turn.Reply;
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
        var independentCharacterArchetypes = new[]
        {
            "Nữ kiếm sĩ lang thang mang theo thanh huyết kiếm phong ấn, đơn độc săn lùng quái thú cổ đại.",
            "Chủ tiệm trà thảo mộc kiêm thầy bói bài Tarot tại phố cổ, luôn thấu suốt tâm can người đối diện.",
            "Tiểu thư quý tộc mê cơ khí ma pháp, bí mật chế tạo khinh khí cầu vượt biển tại xưởng ngầm.",
            "Thủ lĩnh lính đánh thuê thiện chiến, bề ngoài lạnh lùng nhưng nội tâm luôn mang gánh nặng chuộc tội.",
            "Nhà nghiên cứu khảo cổ học dị giới, ngày đêm giải mã tàn tích của nền văn minh đã biến mất.",
            "Nữ hoàng đế quốc cai trị bằng bàn tay sắt, luôn ẩn giấu nỗi cô đơn trên ngai vàng quyền lực.",
            "Nghệ sĩ vĩ cầm thiên tài có tính cách lập dị, chỉ diễn tấu dưới những cơn mưa đêm lạnh giá.",
            "Nữ đặc vụ giải mã công nghệ Cyberpunk, sống ẩn dật giữa khu phố đèn neon rực rỡ.",
            "Nữ pháp sư thời gian trẻ tuổi vô tình làm vỡ đồng hồ cát định mệnh, đang tìm cách hàn gắn thực tại.",
            "Nữ đao phủ hoàng gia bí ẩn luôn đeo mặt nạ bạc, khao khát tìm lại ký ức đã bị phong ấn.",
            "Bác sĩ thú y dịu dàng điều hành phòng khám đêm, chuyên chữa trị cho các linh thú huyền bí.",
            "Nữ đạo tặc bóng đêm chuyên đánh cắp bảo vật của các quý tộc tham nhũng để giúp đỡ khu ổ chuột."
        };

        var pickedFallbacks = independentCharacterArchetypes.OrderBy(_ => Random.Shared.Next()).Take(count).ToList();

        var systemPrompt = CharacterGenerationPrompts.BuildRandomIdeasSystemPrompt(count);
        var userPrompt = $"Hãy sáng tạo đúng {count} ý tưởng nhân vật có bản sắc độc lập, cá tính sắc nét và chiều sâu nội tâm. Mã ngẫu nhiên #{Random.Shared.Next(1000, 9999)}.";

        var list = await _geminiClient.GenerateJsonAsync<List<string>>(
            systemPrompt: systemPrompt,
            userPrompt: userPrompt,
            temperature: 1.15,
            ct: ct);

        if (list != null && list.Count > 0)
        {
            return list;
        }

        return pickedFallbacks;
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
        var dualSystemPrompt = CharacterGenerationPrompts.BuildDualImagePrompt(
            request.Name,
            request.Title,
            request.Category,
            request.PersonalityPrompt,
            request.Idea,
            request.WorldGenre,
            request.VisualIdentity);

        string cleanAvatarPrompt = "";
        string cleanFullBodyPrompt = "";

        try
        {
            var rawDualResult = await _geminiClient.GenerateTextAsync(
                systemPrompt: dualSystemPrompt,
                contents: new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[] { new { text = "Generate the synchronized AVATAR and FULLBODY prompt tags now." } }
                    }
                },
                temperature: 0.7,
                maxOutputTokens: 250,
                ct: ct);

            if (!string.IsNullOrWhiteSpace(rawDualResult))
            {
                var lines = rawDualResult.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("AVATAR:", StringComparison.OrdinalIgnoreCase))
                    {
                        cleanAvatarPrompt = trimmed["AVATAR:".Length..].Trim();
                    }
                    else if (trimmed.StartsWith("FULLBODY:", StringComparison.OrdinalIgnoreCase))
                    {
                        cleanFullBodyPrompt = trimmed["FULLBODY:".Length..].Trim();
                    }
                }
            }
        }
        catch
        {
            // fallback if Gemini prompt tags generation fails
        }

        var isMale = request.VisualIdentity?.Gender?.Equals("Male", StringComparison.OrdinalIgnoreCase) == true;
        var genderTag = isMale ? "1boy" : "1girl";

        // Clean any literal template artifacts from Gemini
        cleanAvatarPrompt = cleanAvatarPrompt
            .Replace("1girl/1boy", genderTag)
            .Replace("<exact hair>", "")
            .Replace("<exact eyes>", "")
            .Replace("<exact face>", "")
            .Replace("<upper outfit details>", "");

        cleanFullBodyPrompt = cleanFullBodyPrompt
            .Replace("1girl/1boy", genderTag)
            .Replace("<exact same hair>", "")
            .Replace("<exact same eyes>", "")
            .Replace("<exact same face>", "")
            .Replace("<exact same intricate outfit>", "");

        if (!cleanAvatarPrompt.Contains("solo", StringComparison.OrdinalIgnoreCase))
        {
            cleanAvatarPrompt = $"masterpiece, best quality, {genderTag}, solo, close-up face portrait, face focus, looking at viewer, " + cleanAvatarPrompt;
        }

        if (!cleanFullBodyPrompt.Contains("solo", StringComparison.OrdinalIgnoreCase))
        {
            cleanFullBodyPrompt = $"masterpiece, best quality, {genderTag}, solo, waist-up standing portrait, sharp focus, " + cleanFullBodyPrompt;
        }

        var avatarRequest = new ImageGenerationRequest(
            Prompt: cleanAvatarPrompt,
            Width: 512,
            Height: 512,
            NegativePrompt: "2girls, 2boys, multiple people, group, crowd, duo, couple, 2persons, extra person, deformed horns, bad anatomy, bad hands, missing fingers, extra digits, cropped, watermark, blurry, low quality, mutated, text, error",
            Workflow: "TextToImage",
            WorkflowVersion: 1
        );

        var fullBodyRequest = new ImageGenerationRequest(
            Prompt: cleanFullBodyPrompt,
            Width: 512,
            Height: 768,
            NegativePrompt: "2girls, 2boys, multiple people, group, crowd, duo, couple, 2persons, extra person, deformed horns, bad anatomy, bad hands, missing fingers, extra digits, cropped, watermark, blurry, low quality, mutated, text, error",
            Workflow: "TextToImage",
            WorkflowVersion: 1
        );

        var avatarUrl = await _imageService.GenerateImageAsync(avatarRequest, ct);
        var fullBodyUrl = await _imageService.GenerateImageAsync(fullBodyRequest, ct);

        return new GenerateAvatarResponse(avatarUrl, cleanAvatarPrompt, avatarUrl, fullBodyUrl, cleanFullBodyPrompt);
    }

    private static string CropFaceAvatarFromMaster(string masterImageUrl)
    {
        if (string.IsNullOrWhiteSpace(masterImageUrl) || !masterImageUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return masterImageUrl;
        }

        try
        {
            var commaIdx = masterImageUrl.IndexOf(',');
            if (commaIdx == -1) return masterImageUrl;

            var base64Data = masterImageUrl[(commaIdx + 1)..];
            var imageBytes = Convert.FromBase64String(base64Data);

            using var image = Image.Load(imageBytes);

            // Calculate upper-center face crop box (52% of canvas, centered horizontally, 3% from top to include crown/hair)
            int cropSize = (int)(Math.Min(image.Width, image.Height) * 0.52);
            int cropX = (image.Width - cropSize) / 2;
            int cropY = (int)(image.Height * 0.03);
            if (cropY + cropSize > image.Height) cropY = 0;

            image.Mutate(ctx => ctx.Crop(new Rectangle(cropX, cropY, cropSize, cropSize)));

            using var ms = new MemoryStream();
            image.SaveAsJpeg(ms);
            var croppedBytes = ms.ToArray();
            return $"data:image/jpeg;base64,{Convert.ToBase64String(croppedBytes)}";
        }
        catch
        {
            return masterImageUrl;
        }
    }

    public async Task<GenerateAvatarResponse> GenerateSceneImageAsync(
        GenerateSceneImageRequest request,
        CancellationToken ct = default)
    {
        var visualDna = "";
        if (request.VisualIdentity != null)
        {
            var v = request.VisualIdentity;
            visualDna = $"""
                Permanent Anatomical DNA (CRITICAL - MUST STRICTLY PRESERVE):
                - Hair: {v.Hair ?? "long blonde wavy hair, floral gold hair ornaments"}
                - Eyes: {v.Eyes ?? "emerald green eyes"}
                - Face: {v.Face ?? "gentle beautiful anime face"}
                - Body Type/Proportions: {v.Body ?? "1m65, slender, graceful figure"}
                """;
        }
        else
        {
            visualDna = """
                Permanent Anatomical DNA (CRITICAL - MUST STRICTLY PRESERVE):
                - Hair: long blonde hair
                - Eyes: emerald green eyes
                - Face: gentle beautiful anime face
                """;
        }

        var sceneStateInfo = "";
        if (request.SceneState != null)
        {
            var s = request.SceneState;
            sceneStateInfo = $"""
                DYNAMIC REAL-TIME PHYSICAL & SPATIAL STATE:
                - Current Location: {s.CurrentLocation ?? "Grand Temple Sanctuary"}
                - Current Position: {s.CurrentPosition ?? "Grand Altar"}
                - Current Active Outfit: {s.CurrentOutfit ?? "Holy silk dress"}
                - Time of Day & Lighting: {s.CurrentTimeOfDay ?? "Sunlit morning"}
                - Held Items: {s.HeldItems ?? "None"}
                - Atmosphere: {s.Atmosphere ?? "Serene"}
                """;
        }
        else
        {
            sceneStateInfo = $"""
                DYNAMIC REAL-TIME PHYSICAL & SPATIAL STATE:
                - Current Location: {request.WorldDescription ?? "Grand Holy Sun Temple Sanctuary"}
                - Current Active Outfit: {request.VisualIdentity?.ClothingStyle ?? "White and gold holy silk dress"}
                - Time of Day & Lighting: Sunlit morning
                """;
        }

        var scenePromptBuilder = $"""
            You are a World-Class Visual Novel & Anime Scene Prompt Engineer for Animagine-XL.
            
            Character:
            - Name: {request.CharacterName ?? "Character"}
            - Title/Role: {request.CharacterTitle ?? "Role"}
            - Lore/Personality: {request.CharacterPersonality ?? "Fascinating character"}
            
            {visualDna}
            
            {sceneStateInfo}
            
            Current Roleplay Dialogue Moment:
            - Player action/dialogue: "{request.UserMessageContent ?? "Interacting together"}"
            - Character action/dialogue: "{request.MessageContent}"
            
            TASK:
            Generate a rich, cohesive English Anime scene prompt (35 - 50 comma-separated tags):
            1. CHARACTER ANCHORS: 1girl, solo, {request.CharacterName ?? "anime character"}, exact hair and eye traits from DNA.
            2. ACTIVE OUTFIT: Render the EXACT current active outfit described above (e.g. if wearing nightgown/swimsuit/holy dress, strictly depict that specific clothing).
            3. POSE & FRAMING: Cowboy shot or full body shot, matching the dialogue action and current physical pose.
            4. CURRENT LOCATION & LIGHTING: Render the exact current location, background scenery, and lighting.
            5. QUALITY: masterpiece, best quality, highly detailed background, cinematic soft lighting, 8k.
            
            Output ONLY the raw comma-separated English tags.
            """;

        string cleanPrompt;
        try
        {
            var contents = new List<object>
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = scenePromptBuilder } }
                }
            };

            var promptResult = await _geminiClient.GenerateTextAsync(
                "You are an expert anime visual novel scene prompt engineer. Output only comma-separated English tags.",
                contents,
                temperature: 0.7,
                maxOutputTokens: 250,
                ct: ct);

            cleanPrompt = (promptResult ?? "1girl, solo, full body, cowboy shot, long blonde hair, emerald green eyes, gold cross earrings, white and gold holy silk dress, white veil, sitting in grand sunlit temple sanctuary, marble pillars, golden sunbeams, holding porcelain tea cup, gentle smile, masterpiece, best quality, 8k")
                .Replace("\n", ", ")
                .Trim();
        }
        catch
        {
            cleanPrompt = $"1girl, solo, full body, cowboy shot, {request.CharacterName ?? "Elysia"}, long blonde hair, emerald green eyes, gold cross earrings, white and gold holy silk dress, white veil, grand sunlit temple sanctuary, marble pillars, golden sunbeams, masterpiece, best quality, 8k";
        }

        var imageReq = new ImageGenerationRequest(
            Prompt: $"{cleanPrompt}, full body, cowboy shot, scenic background, cinematic lighting, ultra-detailed environment, masterpiece, best quality, 8k",
            Width: 1024,
            Height: 768,
            AspectRatio: "16:9",
            ReferenceImageUrl: request.ReferenceImageUrl
        );
        var imageUrl = await _imageService.GenerateImageAsync(imageReq, ct);
        return new GenerateAvatarResponse(imageUrl, cleanPrompt);
    }

    private sealed class RawMemoryExtractionDto
    {
        public List<RawCandidateDto>? Candidates { get; set; }
        public List<RawCandidateDto>? Memories { get; set; }
    }

    private sealed class RawCandidateDto
    {
        public string? Content { get; set; }
        public string? Type { get; set; }
        public int? Importance { get; set; }
        public decimal? Confidence { get; set; }
    }

    public async Task<List<Domain.ValueObjects.MemoryCandidate>> ExtractMemoryCandidatesAsync(
        Character character,
        IReadOnlyCollection<ChatMessageDto> recentMessages,
        CancellationToken ct = default)
    {
        if (recentMessages == null || recentMessages.Count < 2)
        {
            return [];
        }

        var conversationText = string.Join("\n", recentMessages.Select(m => $"{m.Role}: {m.Content}"));
        var systemPrompt = MemoryExtractionPrompts.BuildExtractionSystemPrompt(character);

        var contents = new List<object>
        {
            new
            {
                role = "user",
                parts = new[] { new { text = $"Here is the recent conversation excerpt:\n\n{conversationText}\n\nExtract 0 to 3 memory candidates if applicable." } }
            }
        };

        var rawJson = await _geminiClient.GenerateTextAsync(
            systemPrompt: systemPrompt,
            contents: contents,
            temperature: 0.2,
            maxOutputTokens: 500,
            ct: ct
        );

        if (string.IsNullOrWhiteSpace(rawJson)) return [];

        try
        {
            var cleanJson = rawJson.Trim();
            if (cleanJson.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
                cleanJson = cleanJson.Substring(7);
            if (cleanJson.StartsWith("```"))
                cleanJson = cleanJson.Substring(3);
            if (cleanJson.EndsWith("```"))
                cleanJson = cleanJson.Substring(0, cleanJson.Length - 3);
            cleanJson = cleanJson.Trim();

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<RawMemoryExtractionDto>(cleanJson, options);
            var rawList = result?.Candidates ?? result?.Memories ?? [];

            var candidates = new List<Domain.ValueObjects.MemoryCandidate>();
            foreach (var item in rawList)
            {
                if (string.IsNullOrWhiteSpace(item.Content)) continue;

                if (!Enum.TryParse<MemoryType>(item.Type, true, out var type))
                {
                    continue;
                }

                var importance = item.Importance ?? 3;
                if (importance < 1 || importance > 5)
                {
                    // Strict reject invalid importance
                    continue;
                }

                var confidence = item.Confidence ?? 0.85m;
                if (confidence < 0.0m || confidence > 1.0m)
                {
                    // Strict reject invalid confidence
                    continue;
                }

                try
                {
                    candidates.Add(new Domain.ValueObjects.MemoryCandidate(
                        item.Content.Trim(),
                        type,
                        importance,
                        confidence));
                }
                catch
                {
                    // Skip candidate that fails invariant checks
                }
            }

            return candidates;
        }
        catch
        {
            return [];
        }
    }

    private record ProactiveAiReachoutJsonDto(
        string? OpeningMessage,
        string? MatchReason
    );

    public async Task<ProactiveAiReachoutResult> GenerateProactiveReachoutAsync(
        Character character,
        Domain.Entities.UserProfile userProfile,
        CancellationToken ct = default)
    {
        var userInterests = userProfile.GetInterests();
        var userPersonality = userProfile.GetPersonalityTraits();

        var systemPrompt = $$"""
            You are embodying the character '{{character.Name}}' ({{character.Title}} - Category: {{character.Category}}).
            World / Universe: {{character.WorldName ?? "Modern"}} - {{character.WorldGenre}}
            Character Background & Personality:
            {{character.PersonalityPrompt}}

            SCENARIO:
            You are browsing social profiles or discovering new people in your world.
            You just stumbled upon the personal profile of a user named '{{userProfile.DisplayName}}'.

            USER PROFILE DETAILS:
            - Display Name: {{userProfile.DisplayName}}
            - Bio / Status: {{userProfile.Bio ?? "Không có"}} | Status: {{userProfile.StatusMessage ?? "Trực tuyến"}}
            - Interests / Tags: {{(userInterests.Count > 0 ? string.Join(", ", userInterests) : "Không có")}}
            - Personality Traits: {{(userPersonality.Count > 0 ? string.Join(", ", userPersonality) : "Thân thiện")}}

            TASK:
            1. Find a compelling hook, shared interest, or intriguing detail from the user's profile that catches your character's eye.
            2. In 100% authentic character voice, craft a charming, spontaneous, and natural opening direct message (DM) to reach out and say hello to this user (40 - 80 words).
            3. Use subtle action tags in *asterisks* (e.g. *[curious]..., *[gentle]..., *[playful]...) combined with spoken dialogue.

            OUTPUT JSON FORMAT:
            {
              "openingMessage": "Your spontaneous in-character opening DM message to the user...",
              "matchReason": "Short explanation of why your character was drawn to message this user (e.g. 'Cùng thích nghe nhạc Lofi và nuôi mèo')"
            }
            """;

        var rawResponse = await _geminiClient.GenerateTextAsync(
            systemPrompt: systemPrompt,
            contents: [new { role = "user", parts = new[] { new { text = "Please review this user profile and send your opening message." } } }],
            temperature: 0.85,
            maxOutputTokens: 500,
            ct: ct);

        try
        {
            var cleaned = rawResponse.Trim();
            if (cleaned.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(7);
            }
            else if (cleaned.StartsWith("```", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(3);
            }
            if (cleaned.EndsWith("```", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(0, cleaned.Length - 3);
            }
            cleaned = cleaned.Trim();

            var parsed = JsonSerializer.Deserialize<ProactiveAiReachoutJsonDto>(cleaned, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (parsed != null && !string.IsNullOrWhiteSpace(parsed.OpeningMessage))
            {
                return new ProactiveAiReachoutResult(
                    parsed.OpeningMessage.Trim(),
                    parsed.MatchReason?.Trim() ?? "Tìm thấy điểm chung trên hồ sơ"
                );
            }
        }
        catch
        {
            // Fallback gracefully
        }

        var fallbackMessage = $"*[curious] lướt thấy trang cá nhân của bạn, khẽ mỉm cười gõ phím* Chào {userProfile.DisplayName} nhé! Tình cờ thấy bạn cũng có nhiều sở thích thú vị, chúng ta làm quen được không?";
        return new ProactiveAiReachoutResult(fallbackMessage, "Quan tâm đến hồ sơ cá nhân");
    }
}
