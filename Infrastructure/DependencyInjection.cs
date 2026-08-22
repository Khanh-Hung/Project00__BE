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

        // 3. Add Auth Services (Hasher & JWT)
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
        services.AddScoped<IImageGenerationJobHandler, Infrastructure.Services.ImageGenerationJobHandler>();

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
