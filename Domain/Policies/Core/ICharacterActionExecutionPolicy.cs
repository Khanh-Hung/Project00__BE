using Domain.ValueObjects;

namespace Domain.Policies;

public interface ICharacterActionExecutionPolicy
{
    CharacterStateDelta CalculateDelta(CharacterActionProposal proposal);
}
