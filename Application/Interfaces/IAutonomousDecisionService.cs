using Application.Contracts.Autonomous;

namespace Application.Interfaces;

public interface IAutonomousDecisionService
{
    Task<AutonomousDecisionResult> DecideNextActionAsync(
        AutonomousDecisionRequest request,
        CancellationToken ct = default);
}
