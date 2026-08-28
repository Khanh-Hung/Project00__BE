using Application.Contracts.Autonomy;

namespace Application.Interfaces;

/// <summary>
/// Pure, thin orchestrator coordinating the autonomous lifecycle of a character for a single tick:
/// Context Loading -> Perception/Reaction (if WorldEvent present) -> Autonomous Decision -> Activity Execution -> State/Goal/Scene Updates.
/// </summary>
public interface IAutonomousCharacterLifecycleOrchestrator
{
    Task<AutonomyTickResult> ExecuteTickAsync(
        AutonomyTickRequest request,
        CancellationToken ct = default);
}
