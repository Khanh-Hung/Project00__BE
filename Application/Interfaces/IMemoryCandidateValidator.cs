using Domain.ValueObjects;

namespace Application.Interfaces;

public interface IMemoryCandidateValidator
{
    bool Validate(MemoryCandidate candidate, out string? failureReason);
}
