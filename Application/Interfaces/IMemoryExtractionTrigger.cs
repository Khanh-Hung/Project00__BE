using Application.DTOs;

namespace Application.Interfaces;

public interface IMemoryExtractionTrigger
{
    /// <summary>
    /// Checks policy (e.g. trigger policy after every N messages) and enqueues extraction asynchronously.
    /// </summary>
    bool NotifyMessageSent(MemoryExtractionJob job);
}
