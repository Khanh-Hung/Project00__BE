using Domain.Entities;

namespace Application.Interfaces;

public interface ILorebookEngine
{
    Task<IReadOnlyList<LorebookEntry>> MatchLorebookEntriesAsync(
        Guid characterId,
        string userMessage,
        IReadOnlyList<ChatMessage> recentMessages,
        int maxTokenBudget = 800,
        CancellationToken ct = default);
}
