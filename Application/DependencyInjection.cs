using System.Reflection;
using Application.Behaviors;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();

        // 1. Add MediatR & Pipeline Behaviors
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(assembly);
            cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
            cfg.AddOpenBehavior(typeof(ExceptionHandlingBehavior<,>));
        });

        // 2. Add FluentValidation Validators
        services.AddValidatorsFromAssembly(assembly);

        // 3. Add Memory Candidate Validator, Context Engine, Lorebook, Visual/Voice Compilers & Character Runtime
        services.AddSingleton<Interfaces.IMemoryCandidateValidator, Services.MemoryCandidateValidator>();
        services.AddScoped<Interfaces.ILorebookEngine, Services.LorebookEngine>();
        services.AddScoped<Interfaces.IRoleplayContextEngine, Services.RoleplayContextEngine>();
        services.AddSingleton<Interfaces.IVisualPromptCompiler, Services.VisualPromptCompiler>();
        services.AddSingleton<Interfaces.IVisualGenerationProfileProvider, Services.VisualGenerationProfileProvider>();
        services.AddScoped<Interfaces.IVisualStateResolver, Services.VisualStateResolver>();
        services.AddSingleton<Interfaces.IVoicePromptCompiler, Services.VoicePromptCompiler>();
        services.AddScoped<Interfaces.ICharacterRuntime, Services.CharacterRuntime>();

        return services;
    }
}
