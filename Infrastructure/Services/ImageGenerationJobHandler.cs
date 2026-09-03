using Application.DTOs;
using Application.Enums;
using Application.Interfaces;
using Application.Services;
using Domain.Common.DateTimes;
using Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Handler adapter implementing IImageGenerationJobHandler by delegating to IImageGenerationOrchestrator.
/// </summary>
public sealed class ImageGenerationJobHandler : IImageGenerationJobHandler
{
    private readonly IImageGenerationOrchestrator _orchestrator;

    [ActivatorUtilitiesConstructor]
    public ImageGenerationJobHandler(IImageGenerationOrchestrator orchestrator)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
    }

    /// <summary>
    /// Backwards-compatible constructor allowing direct instantiation in tests.
    /// </summary>
    public ImageGenerationJobHandler(
        CoreDbContext dbContext,
        IVisualPromptCompiler visualCompiler,
        IImageGenerationService imageService,
        ILogger<ImageGenerationJobHandler> logger,
        IDateTimeProvider dateTimeProvider,
        IIdentityQualityEvaluator qualityEvaluator,
        IdentityQualityGuardPolicy? qualityGuardPolicy = null)
    {
        _orchestrator = new ImageGenerationOrchestrator(
            dbContext: dbContext,
            visualCompiler: visualCompiler,
            imageService: imageService,
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<ImageGenerationOrchestrator>.Instance,
            dateTimeProvider: dateTimeProvider,
            qualityEvaluator: qualityEvaluator,
            qualityGuardPolicy: qualityGuardPolicy ?? IdentityQualityGuardPolicy.Default,
            lineageResolver: new PredecessorLineageResolver(dbContext, Microsoft.Extensions.Logging.Abstractions.NullLogger<PredecessorLineageResolver>.Instance),
            acceptanceService: new ArtifactAcceptanceService(dbContext, dateTimeProvider, Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtifactAcceptanceService>.Instance)
        );
    }

    public Task<JobExecutionResult> HandleSceneImageGenerationAsync(
        SceneImageGenerationOutboxPayload payload,
        Guid outboxId,
        string workerId,
        DateTime now,
        CancellationToken ct = default)
    {
        return _orchestrator.OrchestrateSceneImageGenerationAsync(payload, outboxId, workerId, now, ct);
    }
}
