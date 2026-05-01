using Microsoft.EntityFrameworkCore;

namespace GeorgiaPlaces.Infrastructure;

/// <summary>
/// Application DbContext. Schema is defined via EF Core migrations
/// (added later when domain entities land). PostGIS extension is
/// created by infra/postgres/init.sql, not by this context.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("postgis");
        modelBuilder.HasPostgresExtension("pgcrypto");
        base.OnModelCreating(modelBuilder);
    }
}
