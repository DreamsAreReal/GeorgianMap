using Microsoft.Extensions.DependencyInjection;

namespace GeorgiaPlaces.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Use cases / handlers register here as the layer grows.
        return services;
    }
}
