using Application.Interfaces;

namespace Infrastructure.Services;

/// <summary>
/// Default implementation of IImageGenerationExecutorSelector.
/// Currently delegates to the single configured IImageGenerationExecutor in infrastructure.
/// Acts as the execution selection seam without introducing prematurely complex routing or registry frameworks.
/// </summary>
public sealed class ImageGenerationExecutorSelector : IImageGenerationExecutorSelector
{
    private readonly IImageGenerationExecutor _executor;

    public ImageGenerationExecutorSelector(IImageGenerationExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public IImageGenerationExecutor Select(ImageGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _executor;
    }
}
