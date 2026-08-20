using System.Text.Json;
using System.Text.Json.Serialization;
using Application.DTOs;

namespace Application.Common;

public sealed record CharacterStreamTokenData(string Delta);

public sealed record CharacterStreamEvent(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("data")] object Data
)
{
    public static CharacterStreamEvent Token(string delta) =>
        new("token", new CharacterStreamTokenData(delta));

    public static CharacterStreamEvent Metadata(
        string mood,
        int intensity,
        int affectionDelta,
        int affectionScore,
        string relationshipStage,
        Guid characterId,
        Guid userId) =>
        new("metadata", new
        {
            mood,
            intensity,
            affectionDelta,
            affectionScore,
            relationshipStage,
            characterId,
            userId
        });

    public static CharacterStreamEvent EventUnlocked(string eventKey, string context) =>
        new("event_unlocked", new { eventKey, context });

    public static CharacterStreamEvent Done(
        Guid turnId,
        Guid messageId,
        string reply,
        CharacterRelationshipDto relationship,
        IReadOnlyList<CharacterMemoryDto> activeMemories) =>
        new("done", new
        {
            turnId,
            messageId,
            reply,
            relationship,
            activeMemories
        });

    public static CharacterStreamEvent Error(int statusCode, string message) =>
        new("error", new { statusCode, message });

    public string ToSseString()
    {
        var json = JsonSerializer.Serialize(Data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return $"event: {Event}\ndata: {json}\n\n";
    }
}
