using Application.Contracts.Autonomy;

namespace Application.Interfaces;

/// <summary>
/// Loader responsible for gathering and snapshotting all character state, goals, recent memories, and visual context.
/// </summary>
public interface IAutonomousCharacterContextLoader
{
    Task<AutonomousCharacterContext?> LoadContextAsync(
        Guid characterId,
        DateTime currentTime,
        CancellationToken ct = default);
}
