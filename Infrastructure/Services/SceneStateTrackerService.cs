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
            SceneRevision: 1,
            LastUpdatedAt: DateTime.UtcNow
        );

        var delta = await TrackAndExtractDeltaAsync(character, baseState, userMessage, assistantMessage, ct);
        return baseState.ApplyDelta(delta);
    }

    public async Task<SceneStateDelta> TrackAndExtractDeltaAsync(
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
            SceneRevision: 1,
            LastUpdatedAt: DateTime.UtcNow
        );

        var systemPrompt = """
            You are a Visual Continuity & Cinematic Scene Understanding Engine for an interactive roleplay game.
            
            STRICT ANTI-HALLUCINATION & FACTUAL ANCHORING RULES:
            1. "NOTHING CHANGES IN PERSISTENT ROOM STATE UNLESS EXPLICITLY ACTED UPON IN THIS CONVERSATION TURN."
            2. DO NOT invent unmentioned environments, locations, or weathers (e.g. do NOT invent gardens, sunsets, rain, or forests unless explicitly stated in the dialogue or current persistent state).
            3. detailedEnvironment MUST STRICTLY anchor to the current persistent Location and Position (e.g. if in Bedroom, describe bedroom surroundings; do NOT invent an outdoor scene).
            4. lightingStyle MUST STRICTLY anchor to the current persistent TimeOfDay and indoor/outdoor setting (e.g. Daytime indoors -> soft indoor daytime window light; Night indoors -> warm room lamp/candle lighting; do NOT invent sunsets or magical particles unless explicitly triggered by dialogue).
            5. detailedAction MUST ONLY depict the concrete physical actions actually performed in this turn.
            
            Differentiate between:
            - Persistent Room/Location: Macro room/place (e.g. Living Room, Garden, Bedroom, Workshop).
            - Persistent Position: Relative spatial placement (e.g. Beside Window, At Workbench, On Sofa).
            - Persistent Outfit: Current clothing (null if unchanged).
            - Persistent TimeOfDay: Time of day (null if unchanged).
            - Persistent HeldItems: Items picked up or dropped ("none" if released, null if unchanged).
            - Turn-Specific Action: Pose, Action, Expression, Gaze in this specific turn.
            - Deep Cinematic Composition (IN ENGLISH FOR STABLE DIFFUSION CLIP):
              - shotType: "medium shot", "upper body portrait", "close-up portrait", "cowboy shot", "wide shot".
              - cameraAngle: "slight 3/4 turn", "eye level", "dynamic side angle", "from above".
              - subjectPlacement: "centered", "left third", "right third".
              - detailedAction: Concrete English description of the character's physical action in this turn.
              - detailedEnvironment: Fact-anchored English description of background setting based on current room.
              - lightingStyle: Fact-anchored English description of lighting based on current time & room.
              - atmosphere: Fact-anchored English atmosphere/tone.
              - englishPromptTags: 5-10 concise, fact-anchored English anime prompt tags.
            
            Return JSON only matching the schema:
            {
              "locationChange": "Tên địa điểm mới nếu có di chuyển phòng (null nếu ở nguyên phòng cũ)",
              "positionChange": "Vị trí không gian cụ thể mới (ví dụ 'Bên cửa sổ', 'Bên bàn làm việc') hoặc null nếu không đổi",
              "outfitChange": "Trang phục mới nếu có thay đổi (null nếu mặc nguyên đồ cũ)",
              "timeOfDayChange": "Thời điểm mới (null nếu không đổi)",
              "poseChange": "Tư thế cơ thể tức thời (ví dụ 'Đứng', 'Ngồi')",
              "actionChange": "Hành động tức thời cụ thể (ví dụ 'Bước tới che bản vẽ')",
              "expressionChange": "Biểu cảm gương mặt/ánh mắt (ví dụ 'Ánh mắt sắc sảo cảnh giác')",
              "heldItemsChange": "Vật phẩm mới cầm trên tay, hoặc 'none' nếu vừa đặt xuống, hoặc null nếu không đổi",
              "atmosphereChange": "Sắc thái cảm xúc của tình huống này",
              "evidence": "Trích dẫn ngắn chứng minh sự thay đổi",
              "sceneDescription": {
                "shotType": "medium shot",
                "cameraAngle": "slight 3/4 turn",
                "subjectPlacement": "centered",
                "detailedAction": "Fact-anchored English description of action",
                "detailedEnvironment": "Fact-anchored English description of environment",
                "lightingStyle": "Fact-anchored English description of lighting",
                "atmosphere": "English atmosphere",
                "englishPromptTags": ["medium shot", "3/4 turn", "standing", "holding wrench"]
              }
            }
            """;

        var userPrompt = $"""
            Character: {character.Name} ({character.Title})
            
            CURRENT PERSISTENT STATE (Source of Truth):
            - Current Location: {baseState.CurrentLocation}
            - Current Position: {baseState.CurrentPosition}
            - Current Outfit: {baseState.CurrentOutfit}
            - Current Time: {baseState.CurrentTimeOfDay}
            - Held Items: {baseState.HeldItems ?? "None"}
            
            LATEST DIALOGUE TURN:
            - Player: "{userMessage}"
            - {character.Name}: "{assistantMessage}"
            
            Extract the persistent delta and cinematic visual scene understanding in JSON:
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
                maxOutputTokens: 600,
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
                    VisualSceneDescription? sceneDesc = null;
                    if (deltaDto.SceneDescription != null)
                    {
                        var rawDesc = new VisualSceneDescription(
                            shotType: deltaDto.SceneDescription.ShotType,
                            cameraAngle: deltaDto.SceneDescription.CameraAngle,
                            subjectPlacement: deltaDto.SceneDescription.SubjectPlacement,
                            detailedAction: deltaDto.SceneDescription.DetailedAction,
                            detailedEnvironment: deltaDto.SceneDescription.DetailedEnvironment,
                            lightingStyle: deltaDto.SceneDescription.LightingStyle,
                            atmosphere: deltaDto.SceneDescription.Atmosphere,
                            englishPromptTags: deltaDto.SceneDescription.EnglishPromptTags
                        );

                        sceneDesc = VisualSceneDescription.Sanitize(
                            rawDesc,
                            character.VisualIdentity,
                            baseState,
                            userMessage,
                            assistantMessage
                        );
                    }

                    return new SceneStateDelta(
                        LocationChange: deltaDto.LocationChange,
                        PositionChange: deltaDto.PositionChange,
                        OutfitChange: deltaDto.OutfitChange,
                        TimeOfDayChange: deltaDto.TimeOfDayChange,
                        PoseChange: deltaDto.PoseChange,
                        ActionChange: deltaDto.ActionChange,
                        ExpressionChange: deltaDto.ExpressionChange,
                        HeldItemsChange: deltaDto.HeldItemsChange,
                        AtmosphereChange: deltaDto.AtmosphereChange,
                        Evidence: deltaDto.Evidence,
                        SceneDescription: sceneDesc
                    );
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract dynamic scene state delta. Retaining invariant state.");
        }

        return new SceneStateDelta();
    }

    private sealed class SceneStateDeltaDto
    {
        [JsonPropertyName("locationChange")]
        public string? LocationChange { get; set; }

        [JsonPropertyName("positionChange")]
        public string? PositionChange { get; set; }

        [JsonPropertyName("outfitChange")]
        public string? OutfitChange { get; set; }

        [JsonPropertyName("timeOfDayChange")]
        public string? TimeOfDayChange { get; set; }

        [JsonPropertyName("poseChange")]
        public string? PoseChange { get; set; }

        [JsonPropertyName("actionChange")]
        public string? ActionChange { get; set; }

        [JsonPropertyName("expressionChange")]
        public string? ExpressionChange { get; set; }

        [JsonPropertyName("heldItemsChange")]
        public string? HeldItemsChange { get; set; }

        [JsonPropertyName("atmosphereChange")]
        public string? AtmosphereChange { get; set; }

        [JsonPropertyName("evidence")]
        public string? Evidence { get; set; }

        [JsonPropertyName("sceneDescription")]
        public VisualSceneDescriptionDto? SceneDescription { get; set; }
    }

    private sealed class VisualSceneDescriptionDto
    {
        [JsonPropertyName("shotType")]
        public string? ShotType { get; set; }

        [JsonPropertyName("cameraAngle")]
        public string? CameraAngle { get; set; }

        [JsonPropertyName("subjectPlacement")]
        public string? SubjectPlacement { get; set; }

        [JsonPropertyName("detailedAction")]
        public string? DetailedAction { get; set; }

        [JsonPropertyName("detailedEnvironment")]
        public string? DetailedEnvironment { get; set; }

        [JsonPropertyName("lightingStyle")]
        public string? LightingStyle { get; set; }

        [JsonPropertyName("atmosphere")]
        public string? Atmosphere { get; set; }

        [JsonPropertyName("englishPromptTags")]
        public List<string>? EnglishPromptTags { get; set; }
    }
}
