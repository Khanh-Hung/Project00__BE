using System.Collections.Generic;
using Domain.ValueObjects;

namespace Domain.Policies;

public interface ICharacterAppraisalPolicy
{
    CharacterAppraisal Evaluate(
        CharacterInternalExperience experience,
        CharacterBlueprint? blueprint = null);

    IReadOnlyList<CharacterAppraisal> EvaluateAll(
        CharacterInternalExperience experience,
        CharacterBlueprint? blueprint = null);
}
