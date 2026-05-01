using Serilog.Context;

namespace GeorgiaPlaces.Api.Middleware;

/// <summary>
/// Reads incoming X-Correlation-Id header (Cloudflare or upstream client),
/// or generates a fresh ULID-style id, then echoes it back on the response
/// and pushes it into the Serilog context so every log line within the
/// request scope carries it. Per ADR-0006.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        string corrId = ExtractOrGenerate(context);
        context.Response.Headers[HeaderName] = corrId;
        // Make it available to handlers via HttpContext.Items.
        context.Items[HeaderName] = corrId;

        using (LogContext.PushProperty("CorrelationId", corrId))
        {
            await _next(context);
        }
    }

    private static string ExtractOrGenerate(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var values)
            && !string.IsNullOrWhiteSpace(values.ToString()))
        {
            // Trust upstream (Cloudflare / nginx). Cap length to defend against header pollution.
            string supplied = values.ToString();
            return supplied.Length > 128 ? supplied[..128] : supplied;
        }

        // Use TraceIdentifier — already W3C trace-id in modern ASP.NET, ties OTel spans together.
        return context.TraceIdentifier;
    }
}
