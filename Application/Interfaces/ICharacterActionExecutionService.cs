using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.ActionExecution;
using Domain.ValueObjects;

namespace Application.Interfaces;

public interface ICharacterActionExecutionService
{
    Task<CharacterActionExecutionResult> ExecuteAsync(
        Guid characterId,
        CharacterActionProposal proposal,
        CharacterActionExecutionContext context,
        CancellationToken ct = default);
}
