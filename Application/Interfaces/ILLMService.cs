using Domain.Entities;

namespace Application.Interfaces;

public interface ILLMService
{
    Task<string> GenerateRoleplayResponseAsync(
        Character character,
        IReadOnlyCollection<ChatMessage> history,
        string newUserMessage,
        CancellationToken ct = default);
}
