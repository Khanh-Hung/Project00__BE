using Application.Abstractions.Data;
using Application.Interfaces;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // 1. Add EF Core DbContext with PostgreSQL
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        services.AddDbContext<ProjectDbContext>(options =>
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                options.UseNpgsql(connectionString);
            }
            else
            {
                options.UseInMemoryDatabase("ProjectDb");
            }
            options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        });

        // 2. Add UnitOfWork & Current User Provider
        services.AddHttpContextAccessor();
        services.AddScoped<Application.Abstractions.Auth.ICurrentUserProvider, CurrentUserProvider>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // 3. Add Auth Services (Hasher & JWT) and DateTime Provider
        services.AddSingleton<Domain.Common.DateTimes.IDateTimeProvider, Domain.Common.DateTimes.SystemDateTimeProvider>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();

        // 4. Configure JWT Bearer Authentication
        var secret = configuration["Jwt:Secret"] ?? "SuperSecretKeyForNyxorisRoleplayPlatformDev2026!@#";
        var issuer = configuration["Jwt:Issuer"] ?? "NyxorisAuth";
        var audience = configuration["Jwt:Audience"] ?? "NyxorisRoleplay";

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.RequireHttpsMetadata = false;
            options.SaveToken = true;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = issuer,
                ValidAudience = audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                ClockSkew = TimeSpan.Zero
            };
        });

        // 5. Add Storage Services (Local / Cloud)
        services.AddScoped<IStorageService, Infrastructure.Storage.LocalStorageService>();
        services.AddScoped<IVoiceStorage, Infrastructure.Storage.LocalVoiceStorage>();

        // 6. Add Image Generation Service (ComfyUI Provider with Dedicated / Pollinations fallback)
        services.AddHttpClient<Infrastructure.ImageGeneration.PollinationsImageGenerationService>();
        services.AddScoped<Infrastructure.ImageGeneration.PollinationsImageGenerationService>();
        services.AddHttpClient<Infrastructure.ImageGeneration.DedicatedImageGenerationService>();
        services.AddScoped<Infrastructure.ImageGeneration.DedicatedImageGenerationService>();
        services.AddHttpClient<Infrastructure.ImageGeneration.ComfyUI.IComfyUIClient, Infrastructure.ImageGeneration.ComfyUI.ComfyUIClient>(c => c.Timeout = TimeSpan.FromSeconds(120));
        services.AddHttpClient<Infrastructure.ImageGeneration.ComfyUI.IComfyUIInputImageService, Infrastructure.ImageGeneration.ComfyUI.ComfyUIInputImageService>();
        services.AddSingleton<Infrastructure.ImageGeneration.ComfyUI.IComfyUIWorkflowBuilder, Infrastructure.ImageGeneration.ComfyUI.VisualIdentityWorkflowV1Builder>();
        services.AddSingleton<Infrastructure.ImageGeneration.ComfyUI.IComfyUIWorkflowBuilder, Infrastructure.ImageGeneration.ComfyUI.VisualContinuityWorkflowV2Builder>();
        services.AddSingleton<Infrastructure.ImageGeneration.ComfyUI.IComfyUIWorkflowBuilder, Infrastructure.ImageGeneration.ComfyUI.TextToImageWorkflowV1Builder>();
        services.AddScoped<Infrastructure.ImageGeneration.ComfyUIImageGenerationService>();

        var imageProvider = configuration["AiProviders:ImageProvider"];
        if (string.Equals(imageProvider, "ComfyUI", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<IImageGenerationService, Infrastructure.ImageGeneration.ComfyUIImageGenerationService>();
        }
        else
        {
            services.AddScoped<IImageGenerationService, Infrastructure.ImageGeneration.DedicatedImageGenerationService>();
        }

        // Identity Quality Guard & Reference Authority Policy (PR #24)
        var guardPolicy = Application.Services.IdentityQualityGuardPolicy.FromConfiguration(configuration);
        services.AddSingleton(guardPolicy);

        var environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? configuration["Environment"] ?? "Development";
        bool isProduction = string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase);

        var evaluatorType = configuration["AiProviders:ImageGeneration:QualityGuard:EvaluatorType"]
            ?? guardPolicy.EvaluatorType;

        // Check if an IIdentityQualityEvaluator has already been registered in the service collection (e.g. by composition root)
        bool hasCustomEvaluator = services.Any(sd => sd.ServiceType == typeof(IIdentityQualityEvaluator));

        if (!hasCustomEvaluator)
        {
            if (guardPolicy.IsActive)
            {
                if (string.Equals(evaluatorType, "DevelopmentStub", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(evaluatorType, "DevelopmentPassThrough", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(evaluatorType))
                {
                    if (isProduction && !guardPolicy.AllowStubEvaluatorInProduction)
                    {
                        throw new InvalidOperationException(
                            "CRITICAL STARTUP CONFIGURATION ERROR: Quality Guard is enabled (QualityGuard:Enabled=true) in Production environment, but no real IIdentityQualityEvaluator is configured. " +
                            "Production requires a genuine evaluator implementation (e.g. CLIP/ML microservice) registered via services.AddScoped<IIdentityQualityEvaluator, TImplementation>() " +
                            "or explicit opt-in via QualityGuard:AllowStubEvaluatorInProduction=true. " +
                            "To run in development or test mode, set ASPNETCORE_ENVIRONMENT=Development or disable QualityGuard.");
                    }
                    services.AddScoped<IIdentityQualityEvaluator, Infrastructure.Services.DevelopmentPassThroughIdentityQualityEvaluator>();
                }
                else if (string.Equals(evaluatorType, "None", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(evaluatorType, "Disabled", StringComparison.OrdinalIgnoreCase))
                {
                    if (isProduction && !guardPolicy.AllowStubEvaluatorInProduction)
                    {
                        throw new InvalidOperationException(
                            "CRITICAL STARTUP CONFIGURATION ERROR: Quality Guard is enabled (QualityGuard:Enabled=true) but EvaluatorType is set to 'None'/'Disabled'.");
                    }
                    services.AddScoped<IIdentityQualityEvaluator, Infrastructure.Services.DevelopmentPassThroughIdentityQualityEvaluator>();
                }
                else
                {
                    throw new InvalidOperationException(
                        $"QualityGuard is configured with EvaluatorType '{evaluatorType}', but no matching IIdentityQualityEvaluator was registered in the service container. " +
                        "Register your production evaluator implementation via services.AddScoped<IIdentityQualityEvaluator, TImplementation>() in the composition root.");
                }
            }
            else
            {
                services.AddScoped<IIdentityQualityEvaluator, Infrastructure.Services.DevelopmentPassThroughIdentityQualityEvaluator>();
            }
        }

        services.AddScoped<IPredecessorLineageResolver, Infrastructure.Services.PredecessorLineageResolver>();
        services.AddScoped<IArtifactAcceptanceService, Infrastructure.Services.ArtifactAcceptanceService>();
        services.AddScoped<IOutboxLifecycleEventDispatcher, Infrastructure.Services.OutboxLifecycleEventDispatcher>();
        services.AddScoped<IImageGenerationOrchestrator, Infrastructure.Services.ImageGenerationOrchestrator>();
        services.AddScoped<IImageGenerationJobHandler, Infrastructure.Services.ImageGenerationJobHandler>();

        // PR #26: Generation Queue, Recovery, Cancellation & Reliability Services
        services.AddSingleton<IGenerationJobQueue, GenerationQueue>();
        services.AddSingleton(Application.Services.GenerationRetryPolicy.Default);
        services.AddScoped<IGenerationRecoveryService, Infrastructure.Services.GenerationRecoveryService>();
        services.AddScoped<IGenerationCancellationService, Infrastructure.Services.GenerationCancellationService>();
        services.AddScoped<IArtifactReconciliationService, Infrastructure.Services.ArtifactReconciliationService>();
        services.AddHostedService<GenerationWorker>();
        services.AddHostedService<GenerationRecoveryHostedService>();
        services.AddHostedService<ArtifactReconciliationHostedService>();

        // PR #27: Visual Generation Productionization, Observability & Performance
        services.AddSingleton<IGenerationFingerprintService, Application.Services.GenerationFingerprintService>();
        services.AddSingleton(Application.Services.GenerationRetryBudget.Default);
        services.AddSingleton<IGenerationMetrics, Infrastructure.Telemetry.GenerationMetrics>();

        // PR #28: Visual Session Integration & Character Image Lifecycle
        services.AddScoped<IVisualPredecessorResolver, Application.Services.VisualPredecessorResolver>();
        services.AddScoped<IVisualHistoryService, Application.Services.VisualHistoryService>();
        services.AddScoped<IArtifactRetentionService, Application.Services.ArtifactRetentionService>();
        services.AddHostedService<VisualArtifactCleanupWorker>();

        // PR #29: Artifact Reference Integrity & Visual State Consistency
        services.AddScoped<IVisualStateConsistencyService, Application.Services.VisualStateConsistencyService>();

        // PR #30: Persistent Character Visual Identity & Visual Memory
        services.AddScoped<ICharacterVisualProfileService, Infrastructure.Services.CharacterVisualProfileService>();
        services.AddScoped<ICharacterVisualReferenceService, Infrastructure.Services.CharacterVisualReferenceService>();
        services.AddScoped<ICharacterVisualReferenceResolver, Infrastructure.Services.CharacterVisualReferenceResolver>();
        services.AddScoped<IVisualEvidenceRecorder, Infrastructure.Services.VisualEvidenceRecorder>();

        // PR #31: Scene Composition & Generation Context
        services.AddScoped<ICharacterVisualProfileReader, Infrastructure.Services.Scene.CharacterVisualProfileReader>();
        services.AddScoped<IVisualMemoryReader, Infrastructure.Services.Scene.VisualMemoryReader>();
        services.AddScoped<ICanonicalReferenceReader, Infrastructure.Services.Scene.CanonicalReferenceReader>();
        services.AddScoped<IPreviousSceneReader, Infrastructure.Services.Scene.PreviousSceneReader>();
        services.AddScoped<ISceneComposer, Application.Services.SceneComposer>();
        services.AddScoped<IVisualContextResolver, Application.Services.VisualContextResolver>();
        services.AddScoped<IScenePromptComposer, Application.Services.ScenePromptComposer>();
        services.AddScoped<Application.Services.SceneGenerationRequestMapper>();

        // 7. Add Voice Generation & Provider Services (Phase 7 / PR #15)
        services.AddScoped<IVoiceProvider, Infrastructure.Services.MockVoiceProvider>();
        services.AddScoped<IVoiceGenerationService, Infrastructure.Services.VoiceGenerationService>();

        // 7. Add LLM Services & Prompt Compiler
        services.AddSingleton<IPromptCompiler, Infrastructure.LLM.Prompts.PromptCompiler>();
        services.AddHttpClient<Infrastructure.LLM.Core.GeminiApiClient>();
        services.AddScoped<ILLMService, Infrastructure.LLM.LLMService>();
        services.AddScoped<ISceneStateTrackerService, SceneStateTrackerService>();

        // 8. Add Memory Services (Phase 2 - Character Memory System)
        services.Configure<Application.DTOs.MemoryExtractionOptions>(configuration.GetSection("MemoryExtraction"));
        services.AddHttpClient<IEmbeddingService, EmbeddingService>();
        services.AddScoped<IMemoryService, MemoryService>();
        services.AddSingleton<MemoryExtractionBackgroundService>();
        services.AddSingleton<IMemoryExtractionTrigger>(sp => sp.GetRequiredService<MemoryExtractionBackgroundService>());
        services.AddHostedService(sp => sp.GetRequiredService<MemoryExtractionBackgroundService>());

        // 9. Add Transactional Outbox Background Processor (Phase 9)
        services.AddHostedService<OutboxProcessorBackgroundService>();

        return services;
    }
}
