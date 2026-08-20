using Domain.Entities;

namespace Application.Common;

public sealed record RoleplayContext(
    Character Character,
    CharacterRelationship? Relationship,
    IReadOnlyList<CharacterMemory> Memories,
    IReadOnlyList<ChatMessage> RecentMessages,
    string UserMessage,
    ChatSession Session
);
