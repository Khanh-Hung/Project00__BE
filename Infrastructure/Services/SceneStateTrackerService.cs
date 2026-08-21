using Application.Interfaces;
using Domain.Entities;
using Domain.ValueObjects;
using Infrastructure.LLM.Core;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Infrastructure.Services;

public sealed class SceneStateTrackerService : ISceneStateTrackerService
{
    private readonly GeminiApiClient _geminiClient;
    private readonly ILogger<SceneStateTrackerService> _logger;

    public SceneStateTrackerService(GeminiApiClient geminiClient, ILogger<SceneStateTrackerService> logger)
    {
        _geminiClient = geminiClient;
        _logger = logger;
    }

    public async Task<SessionSceneState> TrackAndExtractStateAsync(
        Character character,
        SessionSceneState? currentState,
        string userMessage,
        string assistantMessage,
        CancellationToken ct = default)
    {
        var defaultLocation = !string.IsNullOrWhiteSpace(character.WorldName) ? $"{character.WorldName}" : "Thánh Điện Thần Mặt Trời";
        var defaultOutfit = character.VisualIdentity?.ClothingStyle ?? "Thánh phục lụa trắng thêu viền vàng kim quý phái, khăn voan trắng";
        var defaultTime = "Bình minh rạng rỡ";

        var baseState = currentState ?? new SessionSceneState(
            CurrentLocation: defaultLocation,
            CurrentPosition: "Đại điện",
            CurrentOutfit: defaultOutfit,
            CurrentTimeOfDay: defaultTime,
            HeldItems: null,
            Atmosphere: "Thanh tịnh",
            LastUpdatedAt: DateTime.UtcNow
        );

        var systemPrompt = """
            You are a Visual Continuity Delta Extractor for an interactive roleplay game.
            
            FUNDAMENTAL CONTINUITY INVARIANT:
            1. "NOTHING CHANGES UNLESS EXPLICITLY CHANGED IN THIS CONVERSATION TURN."
            2. DO NOT hallucinate or invent changes that were not explicitly mentioned or clearly acted upon in the latest dialogue.
            3. If Location, Outfit, TimeOfDay, or Items did NOT change in this turn, omit them or set them to null.
            4. HeldItems Lifecycle:
               - If an item is picked up/held -> provide item name.
               - If an item is placed down, dropped, or released -> return "none".
               - If held status has not changed -> return null.
            
            Return JSON only matching the schema:
            {
              "locationChange": "Tên địa điểm mới nếu có di chuyển (null nếu ở nguyên vị trí cũ)",
              "outfitChange": "Trang phục mới nếu có thay đổi/cởi bớt/làm ướt (null nếu mặc nguyên đồ cũ)",
              "timeOfDayChange": "Thời điểm mới nếu thời gian trôi qua rõ rệt (null nếu cùng thời điểm)",
              "poseChange": "Tư thế/hành động vật lý cụ thể trong câu thoại này",
              "heldItemsChange": "Vật phẩm mới cầm trên tay, hoặc 'none' nếu vừa đặt xuống, hoặc null nếu không đổi",
              "atmosphereChange": "Sắc thái cảm xúc của tình huống này",
              "evidence": "Trích dẫn ngắn chứng minh sự thay đổi"
            }
            """;

        var userPrompt = $"""
            Character: {character.Name} ({character.Title})
            
            CURRENT PERSISTENT STATE (Source of Truth):
            - Current Location: {baseState.CurrentLocation}
            - Current Outfit: {baseState.CurrentOutfit}
            - Current Time: {baseState.CurrentTimeOfDay}
            - Current Position: {baseState.CurrentPosition}
            - Held Items: {baseState.HeldItems ?? "None"}
            
            LATEST DIALOGUE TURN:
            - Player: "{userMessage}"
            - {character.Name}: "{assistantMessage}"
            
            Did this specific turn contain explicit evidence of changing location, outfit, time, pose, or held items?
            Extract the DELTA in JSON:
            """;

        try
        {
            var contents = new List<object>
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = userPrompt } }
                }
            };

            var jsonResult = await _geminiClient.GenerateTextAsync(
                systemPrompt,
                contents,
                temperature: 0.1,
                maxOutputTokens: 300,
                ct: ct);

            if (!string.IsNullOrWhiteSpace(jsonResult))
            {
                var cleanJson = jsonResult.Trim();
                if (cleanJson.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
                {
                    cleanJson = cleanJson.Substring(7);
                }
                if (cleanJson.StartsWith("```", StringComparison.OrdinalIgnoreCase))
                {
                    cleanJson = cleanJson.Substring(3);
                }
                if (cleanJson.EndsWith("```", StringComparison.OrdinalIgnoreCase))
                {
                    cleanJson = cleanJson.Substring(0, cleanJson.Length - 3);
                }
                cleanJson = cleanJson.Trim();

                var deltaDto = JsonSerializer.Deserialize<SceneStateDeltaDto>(cleanJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (deltaDto != null)
                {
                    var delta = new SceneStateDelta(
                        LocationChange: deltaDto.LocationChange,
                        OutfitChange: deltaDto.OutfitChange,
                        TimeOfDayChange: deltaDto.TimeOfDayChange,
                        PoseChange: deltaDto.PoseChange,
                        HeldItemsChange: deltaDto.HeldItemsChange,
                        AtmosphereChange: deltaDto.AtmosphereChange,
                        Evidence: deltaDto.Evidence
                    );

                    // Apply the mathematical merge: NewState = OldState ⊕ Delta
                    return baseState.ApplyDelta(delta);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract dynamic scene state delta. Retaining invariant state.");
        }

        return baseState;
    }

    private sealed class SceneStateDeltaDto
    {
        [JsonPropertyName("locationChange")]
        public string? LocationChange { get; set; }

        [JsonPropertyName("outfitChange")]
        public string? OutfitChange { get; set; }

        [JsonPropertyName("timeOfDayChange")]
        public string? TimeOfDayChange { get; set; }

        [JsonPropertyName("poseChange")]
        public string? PoseChange { get; set; }

        [JsonPropertyName("heldItemsChange")]
        public string? HeldItemsChange { get; set; }

        [JsonPropertyName("atmosphereChange")]
        public string? AtmosphereChange { get; set; }

        [JsonPropertyName("evidence")]
        public string? Evidence { get; set; }
    }
}
