using Application.Common;
using Application.DTOs;

namespace Application.Interfaces;

public sealed record CharacterTurnOptions(
    bool GenerateVoice = false,
    bool GenerateImage = false
);

public sealed record CharacterTurnRequest(
    Guid UserId,
    Guid CharacterId,
    Guid SessionId,
    string UserMessage,
    Guid? TurnId = null,
    CharacterTurnOptions? Options = null
);

public sealed record CharacterTurnResult(
    Guid MessageId,
    Guid TurnId,
    string Reply,
    CharacterRelationshipDto Relationship,
    IReadOnlyList<CharacterMemoryDto> ActiveMemories,
    string? AudioUrl = null,
    string? ImageUrl = null,
    string Mood = "Neutral",
    int MoodIntensity = 50,
    int AffectionDelta = 0
);

public interface ICharacterRuntime
{
    Task<CharacterTurnResult> ProcessTurnAsync(
        CharacterTurnRequest request,
        CancellationToken ct = default);

    IAsyncEnumerable<CharacterStreamEvent> ProcessTurnStreamAsync(
        CharacterTurnRequest request,
        CancellationToken ct = default);
}
