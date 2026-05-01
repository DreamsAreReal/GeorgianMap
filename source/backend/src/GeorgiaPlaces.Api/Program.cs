using GeorgiaPlaces.Api.Endpoints;
using GeorgiaPlaces.Api.Middleware;
using GeorgiaPlaces.Application;
using GeorgiaPlaces.Infrastructure;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ─── Sentry: read DSN from file (Docker secret), fall back to env (DSN), else off ────
string? sentryDsn = ReadSecretOrEnv("SENTRY_DSN_FILE", "SENTRY_DSN");

// ─── Serilog: structured logs to console + OTLP (Loki via Grafana Cloud, ADR-0006) ───
builder.Host.UseSerilog((ctx, services, config) =>
{
    config
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithEnvironmentName()
        .Enrich.WithProperty("service", "georgia-places-api")
        .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture);

    string? otlpEndpoint = ctx.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
    string? otlpHeaders = ReadSecretOrEnv("OTEL_EXPORTER_OTLP_HEADERS_FILE", "OTEL_EXPORTER_OTLP_HEADERS");
    if (!string.IsNullOrWhiteSpace(otlpEndpoint))
    {
        config.WriteTo.OpenTelemetry(opts =>
        {
            opts.Endpoint = otlpEndpoint;
            opts.Protocol = Serilog.Sinks.OpenTelemetry.OtlpProtocol.HttpProtobuf;
            if (!string.IsNullOrWhiteSpace(otlpHeaders))
            {
                opts.Headers = ParseHeaderString(otlpHeaders);
            }
            opts.ResourceAttributes = new Dictionary<string, object>
            {
                ["service.name"] = "georgia-places-api",
                ["deployment.environment"] = ctx.HostingEnvironment.EnvironmentName,
            };
        });
    }
});

// ─── Sentry ────────────────────────────────────────────────────────────────────
builder.WebHost.UseSentry(o =>
{
    o.Dsn = sentryDsn ?? string.Empty;
    o.TracesSampleRate = 0.1;
    o.SendDefaultPii = false;  // anonymous-by-design (TZ §1.2)
    o.AttachStacktrace = true;
});

// ─── OpenTelemetry: traces + metrics → Grafana Cloud OTLP ──────────────────────
{
    string? otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
    string? otlpHeaders = ReadSecretOrEnv("OTEL_EXPORTER_OTLP_HEADERS_FILE", "OTEL_EXPORTER_OTLP_HEADERS");

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(
            serviceName: "georgia-places-api",
            serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
        .WithTracing(t => t
            .AddAspNetCoreInstrumentation(opts =>
            {
                opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/api/v1/health");
            })
            .AddHttpClientInstrumentation()
            .AddSource("GeorgiaPlaces.*")
            .AddOtlpExporter(opt =>
            {
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    opt.Endpoint = new Uri(otlpEndpoint);
                    opt.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                    if (!string.IsNullOrWhiteSpace(otlpHeaders))
                    {
                        opt.Headers = otlpHeaders;
                    }
                }
            }))
        .WithMetrics(m => m
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter("GeorgiaPlaces.*")
            .AddOtlpExporter(opt =>
            {
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    opt.Endpoint = new Uri(otlpEndpoint);
                    opt.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                    if (!string.IsNullOrWhiteSpace(otlpHeaders))
                    {
                        opt.Headers = otlpHeaders;
                    }
                }
            }));
}

// ─── ProblemDetails (RFC 7807) per TZ §8.0 / ADR-0004 ──────────────────────────
builder.Services.AddProblemDetails(opts =>
{
    opts.CustomizeProblemDetails = ctx =>
    {
        if (ctx.HttpContext.Items.TryGetValue(CorrelationIdMiddleware.HeaderName, out var corr) && corr is string s)
        {
            ctx.ProblemDetails.Extensions["correlationId"] = s;
        }
        ctx.ProblemDetails.Extensions["instance"] ??= ctx.HttpContext.Request.Path.Value;
    };
});
builder.Services.AddExceptionHandler<GeorgiaPlaces.Api.ProblemDetailsExceptionHandler>();

// ─── HttpClient with Polly resilience (per TZ §6.6, §7.4 — closes 1 of 2 remaining P0s) ───
builder.Services.AddHttpClient("ExternalDefault")
    .AddStandardResilienceHandler(opts =>
    {
        opts.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
        opts.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
        opts.Retry.MaxRetryAttempts = 2;
        opts.Retry.UseJitter = true;
        opts.CircuitBreaker.FailureRatio = 0.5;
        opts.CircuitBreaker.MinimumThroughput = 10;
        opts.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        opts.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
    });

// ─── App layers ────────────────────────────────────────────────────────────────
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// ─── Health checks ─────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddDbContextCheck<GeorgiaPlaces.Infrastructure.AppDbContext>(
        name: "postgres",
        tags: ["ready"]);

// ─── CORS — exposes correlation header to browser JS (P1 from 2nd review) ──────
const string CorsPolicy = "GeorgiaPlacesCors";
builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p => p
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .WithMethods("GET", "POST", "PUT", "OPTIONS")
    .WithExposedHeaders(CorrelationIdMiddleware.HeaderName)));

// ─── OpenAPI ───────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseSerilogRequestLogging(opts =>
{
    opts.GetLevel = (httpCtx, elapsed, ex) => ex is not null
        ? Serilog.Events.LogEventLevel.Error
        : httpCtx.Response.StatusCode >= 500
            ? Serilog.Events.LogEventLevel.Error
            : httpCtx.Response.StatusCode >= 400
                ? Serilog.Events.LogEventLevel.Warning
                : Serilog.Events.LogEventLevel.Information;
});

app.UseExceptionHandler();      // wires ProblemDetails RFC 7807 for unhandled exceptions
app.UseStatusCodePages();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseCors(CorsPolicy);

app.MapHealthEndpoints();
app.MapOpenApi();

app.Run();

// ─── helpers ───────────────────────────────────────────────────────────────────
static string? ReadSecretOrEnv(string filePathEnvVar, string fallbackEnvVar)
{
    string? path = Environment.GetEnvironmentVariable(filePathEnvVar);
    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
    {
        return File.ReadAllText(path).Trim();
    }
    return Environment.GetEnvironmentVariable(fallbackEnvVar);
}

static IDictionary<string, string> ParseHeaderString(string raw)
{
    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        int eq = pair.IndexOf('=');
        if (eq > 0)
        {
            dict[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
        }
    }
    return dict;
}

// Marker for WebApplicationFactory<Program>.
public partial class Program;
