using System;
using Domain.ValueObjects;

namespace Application.Contracts.Safety;

public sealed record CharacterSafetyContext(
    Guid CharacterId,
    CharacterStateSnapshot CharacterState);
