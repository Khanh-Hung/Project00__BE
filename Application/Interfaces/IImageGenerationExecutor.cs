using Domain.ValueObjects;

namespace Application.Interfaces;

/// <summary>
/// Execution engine abstraction decoupled from specific providers (e.g. ComfyUI, Native engine, External API).
/// Responsible for executing a validated ImageGenerationRequest on a runtime engine.
/// </summary>
public interface IImageGenerationExecutor
{
    /// <summary>
    /// Executes the image generation request on the underlying execution engine.
    /// </summary>
    Task<ImageGenerationResult> ExecuteAsync(
        ImageGenerationRequest request,
        CancellationToken ct = default);
}
