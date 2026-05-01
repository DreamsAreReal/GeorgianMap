using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GeorgiaPlaces.Api.Endpoints;

/// <summary>
/// /health/live — shallow liveness (process up, no deps).
/// /health/ready — deep readiness (Postgres reachable).
/// /api/v1/health — versioned shallow ping for monitoring services.
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Versioned shallow probe. Used by docker-compose healthcheck and UptimeRobot.
        app.MapGet("/api/v1/health", () => Results.Ok(new { status = "ok" }))
            .WithName("HealthShallow")
            .WithTags("health");

        // Liveness — never touches DB. Useful for Kubernetes-style restarts.
        app.MapHealthChecks("/api/v1/health/live", new()
        {
            Predicate = _ => false,  // run zero checks; just return 200 if process is up
            ResponseWriter = HealthResponseWriter.WriteJson,
        });

        // Readiness — checks tagged "ready" (DB, etc.). Returns 503 if any unhealthy.
        app.MapHealthChecks("/api/v1/health/ready", new()
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = HealthResponseWriter.WriteJson,
        });

        return app;
    }
}

internal static class HealthResponseWriter
{
    public static Task WriteJson(HttpContext ctx, HealthReport report)
    {
        ctx.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                duration_ms = e.Value.Duration.TotalMilliseconds,
                description = e.Value.Description,
            }),
        };
        return ctx.Response.WriteAsJsonAsync(payload);
    }
}
