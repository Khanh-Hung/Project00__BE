using Application.Abstractions.Data;
using Application.Interfaces;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        });

        // 2. Add UnitOfWork
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // 3. Add External Services (LLM)
        services.AddHttpClient<ILLMService, LLMService>();

        return services;
    }
}
