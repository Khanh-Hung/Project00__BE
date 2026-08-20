using Domain.Enums;

namespace Application.DTOs;

public record LorebookEntryDto(
    Guid Id,
    Guid? CharacterId,
    string Title,
    string Content,
    List<string> Keywords,
    LorebookCategory Category,
    bool IsConstant,
    int Priority,
    bool IsEnabled,
    DateTime CreatedAt
);

public record CreateLorebookEntryRequest(
    Guid? CharacterId,
    string Title,
    string Content,
    List<string>? Keywords,
    LorebookCategory Category = LorebookCategory.Other,
    bool IsConstant = false,
    int Priority = 100
);

public record UpdateLorebookEntryRequest(
    string Title,
    string Content,
    List<string>? Keywords,
    LorebookCategory Category,
    bool IsConstant,
    int Priority,
    bool IsEnabled
);
