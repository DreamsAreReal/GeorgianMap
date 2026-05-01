using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GeorgiaPlaces.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Default is required (set ConnectionStrings__Default env or appsettings).");

        services.AddDbContext<AppDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npg =>
            {
                npg.UseNetTopologySuite();
                npg.EnableRetryOnFailure(maxRetryCount: 3);
            });
        });

        return services;
    }
}
