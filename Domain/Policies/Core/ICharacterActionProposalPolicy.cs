using Domain.ValueObjects;

namespace Domain.Policies;

public interface ICharacterActionProposalPolicy
{
    CharacterActionProposalEvaluation Evaluate(
        CharacterIntentEvaluation intentEvaluation,
        CharacterActionProposalContext context);
}
