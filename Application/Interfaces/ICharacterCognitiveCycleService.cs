using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.CognitiveCycle;

namespace Application.Interfaces;

public interface ICharacterCognitiveCycleService
{
    Task<CharacterCognitiveCycleResult> RunAsync(
        CharacterCognitiveCycleContext context,
        CancellationToken cancellationToken = default);
}
