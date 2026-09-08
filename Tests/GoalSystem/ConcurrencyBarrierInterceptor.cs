using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Tests.GoalSystem;

/// <summary>
/// Interceptor that deterministically suspends execution inside SavingChangesAsync until explicitly released.
/// Used to guarantee race condition arrival ordering across multiple DbContext instances.
/// </summary>
public sealed class ConcurrencyBarrierInterceptor : SaveChangesInterceptor
{
    private readonly TaskCompletionSource<bool> _beforeSaveTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _releaseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitForBeforeSaveAsync() => _beforeSaveTcs.Task;
    public void Release() => _releaseTcs.TrySetResult(true);

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        _beforeSaveTcs.TrySetResult(true);
        await _releaseTcs.Task;
        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
