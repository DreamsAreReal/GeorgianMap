using GeorgiaPlaces.Application.Places;
using GeorgiaPlaces.Infrastructure.Places;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
            // Raw-SQL migrations intentionally diverge from the model snapshot
            // (PostGIS / materialized views aren't supported by EF scaffolding).
            // Suppress the diff check; correctness is enforced by integration tests.
            options.ConfigureWarnings(w => w.Ignore(
                RelationalEventId.PendingModelChangesWarning));
        });

        services.AddScoped<IPlaceReadRepository, PlaceReadRepository>();
        return services;
    }
}
