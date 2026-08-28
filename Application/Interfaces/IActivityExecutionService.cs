using Application.Contracts.Autonomous;

namespace Application.Interfaces;

public interface IActivityExecutionService
{
    Task<ActivityExecutionResult> ExecuteActivityAsync(
        ActivityExecutionRequest request,
        CancellationToken ct = default);
}
