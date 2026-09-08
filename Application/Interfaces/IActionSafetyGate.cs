using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.ValueObjects;

namespace Application.Interfaces;

public interface IActionSafetyGate
{
    Task<SafetyDecision> EvaluateAsync(
        Guid characterId,
        CharacterActionProposal proposal,
        CancellationToken cancellationToken = default);
}
