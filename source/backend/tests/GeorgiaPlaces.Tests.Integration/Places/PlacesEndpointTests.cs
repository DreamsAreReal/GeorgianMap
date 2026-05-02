using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace GeorgiaPlaces.Tests.Integration.Places;

public class PlacesEndpointTests : IAsyncLifetime
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

        // Pre-create extensions on this DB so EF migrations can declare them.
        await using (var bootstrap = new Npgsql.NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await bootstrap.OpenAsync();
            await using var cmd = bootstrap.CreateCommand();
            cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS pgcrypto;";
            await cmd.ExecuteNonQueryAsync();
        }

        Environment.SetEnvironmentVariable("ConnectionStrings__Default", _postgres.GetConnectionString());
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Development"));

        // Seed: insert several places + refresh the materialized view.
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        await _postgres.DisposeAsync();
    }

    private async Task SeedAsync()
    {
        await using var c = new Npgsql.NpgsqlConnection(_postgres.GetConnectionString());
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO places (name, geom, category, attributes, data_freshness_score) VALUES
              ('Икалто',         ST_MakePoint(45.21, 41.85)::geography, 'monastery',
                  '{"free":true, "parking":true}'::jsonb, 0.9),
              ('Шуамта',         ST_MakePoint(45.30, 41.90)::geography, 'monastery',
                  '{"free":true}'::jsonb,                           0.8),
              ('Гергети',        ST_MakePoint(44.62, 42.66)::geography, 'monastery',
                  '{"free":true, "road":"unpaved"}'::jsonb,         0.7),
              ('Ananuri',        ST_MakePoint(44.70, 42.16)::geography, 'fortress',
                  '{"free":false, "price_gel":15}'::jsonb,          0.95),
              ('Hidden place',   ST_MakePoint(44.80, 41.71)::geography, 'park',
                  '{}'::jsonb,                                      0.5);
            UPDATE places SET hidden = true WHERE name = 'Hidden place';
            REFRESH MATERIALIZED VIEW places_summary;
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Returns_all_visible_places_with_no_filter()
    {
        var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/api/v1/places?limit=10");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("places").GetArrayLength().Should().Be(4);    // Hidden excluded
        body.GetProperty("hasMore").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Filters_by_category()
    {
        var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/api/v1/places?category=fortress");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("places").GetArrayLength().Should().Be(1);
        body.GetProperty("places")[0].GetProperty("name").GetString().Should().Be("Ananuri");
    }

    [Fact]
    public async Task Filters_by_attrs_dsl()
    {
        var client = _factory!.CreateClient();
        // free:true matches 3 monasteries
        var resp = await client.GetAsync("/api/v1/places?attrs=free:true");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("places").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task Bbox_filter_returns_only_inside()
    {
        var client = _factory!.CreateClient();
        // Tight bbox around Икалто & Шуамта (lat ~41.85-41.90, lng ~45.21-45.30)
        var resp = await client.GetAsync("/api/v1/places?bbox=41.80,45.15,41.95,45.35");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("places").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Cursor_pagination_returns_next_page()
    {
        var client = _factory!.CreateClient();
        var p1 = await client.GetFromJsonAsync<JsonElement>("/api/v1/places?limit=2");
        p1.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        string? cursor = p1.GetProperty("nextCursor").GetString();
        cursor.Should().NotBeNullOrEmpty();

        var p2 = await client.GetFromJsonAsync<JsonElement>($"/api/v1/places?limit=2&cursor={Uri.EscapeDataString(cursor!)}");
        p2.GetProperty("places").GetArrayLength().Should().BeGreaterThan(0);

        var p1Ids = p1.GetProperty("places").EnumerateArray().Select(e => e.GetProperty("id").GetInt64()).ToHashSet();
        var p2Ids = p2.GetProperty("places").EnumerateArray().Select(e => e.GetProperty("id").GetInt64()).ToHashSet();
        p1Ids.Intersect(p2Ids).Should().BeEmpty();
    }

    [Fact]
    public async Task Hidden_place_is_never_returned()
    {
        var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/api/v1/places?limit=100");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var names = body.GetProperty("places").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!).ToList();
        names.Should().NotContain("Hidden place");
    }

    [Fact]
    public async Task Invalid_bbox_returns_400_problem_details()
    {
        var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/api/v1/places?bbox=garbage");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Contain("Invalid filter");
        body.GetProperty("field").GetString().Should().Be("bbox");
    }

    [Fact]
    public async Task Cache_control_header_is_set()
    {
        var client = _factory!.CreateClient();
        var resp = await client.GetAsync("/api/v1/places");
        resp.Headers.CacheControl.Should().NotBeNull();
        resp.Headers.CacheControl!.MaxAge.Should().Be(TimeSpan.FromMinutes(5));
        resp.Headers.CacheControl!.SharedMaxAge.Should().Be(TimeSpan.FromHours(1));
    }
}
