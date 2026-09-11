namespace Application.Interfaces;

/// <summary>
/// Execution selection abstraction determining which IImageGenerationExecutor handles a validated request.
/// Decouples ImageGenerationOrchestrator from concrete execution mechanisms.
/// </summary>
public interface IImageGenerationExecutorSelector
{
    /// <summary>
    /// Selects an execution implementation capable of executing the specified generation request.
    /// </summary>
    IImageGenerationExecutor Select(ImageGenerationRequest request);
}
