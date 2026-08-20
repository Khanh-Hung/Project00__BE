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

        // 3. Add Memory Candidate Validator, Context Engine, Visual Prompt Compiler & Voice Prompt Compiler
        services.AddSingleton<Interfaces.IMemoryCandidateValidator, Services.MemoryCandidateValidator>();
        services.AddScoped<Interfaces.IRoleplayContextEngine, Services.RoleplayContextEngine>();
        services.AddSingleton<Interfaces.IVisualPromptCompiler, Services.VisualPromptCompiler>();
        services.AddSingleton<Interfaces.IVoicePromptCompiler, Services.VoicePromptCompiler>();

        return services;
    }
}
