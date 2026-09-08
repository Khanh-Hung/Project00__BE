using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.Safety;
using Application.Interfaces;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.Safety;

public sealed class ActionSafetyGate : IActionSafetyGate
{
    private readonly ICharacterStateService _stateService;
    private readonly IReadOnlyList<IActionSafetyPolicy> _policies;
    private readonly ILogger<ActionSafetyGate> _logger;

    public ActionSafetyGate(
        ICharacterStateService stateService,
        IEnumerable<IActionSafetyPolicy> policies,
        ILogger<ActionSafetyGate> logger)
    {
        _stateService = stateService ?? throw new ArgumentNullException(nameof(stateService));
        _policies = (policies ?? throw new ArgumentNullException(nameof(policies)))
            .OrderBy(p => p.Priority)
            .ToList();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SafetyDecision> EvaluateAsync(
        Guid characterId,
        CharacterActionProposal proposal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (proposal == null)
        {
            return SafetyDecision.Denied("INVALID_ACTION", "Action proposal cannot be null.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var characterState = await _stateService.GetAsync(characterId, cancellationToken);
            if (characterState == null)
            {
                _logger.LogWarning("[ActionSafetyGate] Authoritative character state not found for CharacterId {CharacterId}.", characterId);
                return SafetyDecision.Denied("CHARACTER_NOT_FOUND", $"Authoritative character state not found for CharacterId '{characterId}'.");
            }

            var context = new CharacterSafetyContext(characterId, characterState);

            foreach (var policy in _policies)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var decision = policy.Evaluate(proposal, context);
                if (!decision.IsAllowed)
                {
                    _logger.LogInformation(
                        "[ActionSafetyGate] Policy {Policy} denied action {ActionType} for Character {CharacterId}. Code: {PolicyCode}, Reason: {Reason}",
                        policy.GetType().Name,
                        proposal.Type,
                        characterId,
                        decision.PolicyCode,
                        decision.Reason);

                    return decision;
                }
            }

            return SafetyDecision.Allowed();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[ActionSafetyGate] Unexpected failure during safety evaluation for Character {CharacterId}. Failing closed.",
                characterId);

            return SafetyDecision.Denied("SAFETY_EVALUATION_FAILED", $"Safety evaluation failed unexpectedly: {ex.Message}");
        }
    }
}
