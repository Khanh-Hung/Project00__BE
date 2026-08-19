using Application.DTOs;
using Domain.Entities;

namespace Application.Interfaces;

public interface ILLMService
{
    Task<RoleplayTurnResult> GenerateRoleplayTurnAsync(
        Character character,
        IReadOnlyCollection<ChatMessage> history,
        string newUserMessage,
        ChatSession? session = null,
        CancellationToken ct = default);

    Task<string> GenerateRoleplayResponseAsync(
        Character character,
        IReadOnlyCollection<ChatMessage> history,
        string newUserMessage,
        ChatSession? session = null,
        CancellationToken ct = default);

    Task<GeneratedCharacterDto> GenerateCharacterProfileAsync(
        string idea,
        string? category = null,
        CancellationToken ct = default);

    Task<List<string>> GenerateRandomIdeasAsync(
        int count = 4,
        CancellationToken ct = default);

    Task<List<string>> GenerateRoleplaySuggestionsAsync(
        Character character,
        IReadOnlyCollection<ChatMessage> history,
        CancellationToken ct = default);

    Task<GenerateAvatarResponse> GenerateAvatarAsync(
        GenerateAvatarRequest request,
        CancellationToken ct = default);

    Task<GenerateAvatarResponse> GenerateSceneImageAsync(
        GenerateSceneImageRequest request,
        CancellationToken ct = default);
}
