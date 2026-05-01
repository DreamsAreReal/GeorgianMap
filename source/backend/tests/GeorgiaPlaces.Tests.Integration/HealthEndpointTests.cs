using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

namespace GeorgiaPlaces.Tests.Integration;

/// <summary>
/// Integration tests for /api/v1/health endpoints. Spins up a Postgres container
/// (PostGIS image) so the deep readiness probe has a real DB to ping.
/// </summary>
public class HealthEndpointTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgis/postgis:16-3.4-alpine")
        .WithDatabase("georgia_places_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    private WebApplicationFactory<Program>? _factory;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Default",
            _postgres.GetConnectionString());

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("environment", Environments.Development));
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Shallow_health_returns_200_ok()
    {
        var client = _factory!.CreateClient();
        var response = await client.GetAsync("/api/v1/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Liveness_returns_200_when_process_is_up()
    {
        var client = _factory!.CreateClient();
        var response = await client.GetAsync("/api/v1/health/live");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_returns_200_when_postgres_reachable()
    {
        var client = _factory!.CreateClient();
        var response = await client.GetAsync("/api/v1/health/ready");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Correlation_id_is_echoed_back_when_supplied()
    {
        var client = _factory!.CreateClient();
        const string corr = "test-corr-1234";
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
        req.Headers.Add("X-Correlation-Id", corr);
        var response = await client.SendAsync(req);
        response.Headers.GetValues("X-Correlation-Id").Should().Contain(corr);
    }

    [Fact]
    public async Task Correlation_id_is_generated_when_missing()
    {
        var client = _factory!.CreateClient();
        var response = await client.GetAsync("/api/v1/health");
        response.Headers.GetValues("X-Correlation-Id").Should().NotBeEmpty();
    }
}
