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

        // 5. Add Storage Service (Local / Cloud)
        services.AddScoped<IStorageService, Infrastructure.Storage.LocalStorageService>();

        // 6. Add Image Generation Service (Dedicated AI Server with Fallback)
        services.AddHttpClient<Infrastructure.ImageGeneration.PollinationsImageGenerationService>();
        services.AddScoped<Infrastructure.ImageGeneration.PollinationsImageGenerationService>();
        services.AddHttpClient<Infrastructure.ImageGeneration.DedicatedImageGenerationService>();
        services.AddScoped<IImageGenerationService, Infrastructure.ImageGeneration.DedicatedImageGenerationService>();

        // 7. Add Voice Generation Service (Phase 7 - Character Voice & Audio)
        services.AddHttpClient<Infrastructure.Services.VoiceGenerationService>();
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
