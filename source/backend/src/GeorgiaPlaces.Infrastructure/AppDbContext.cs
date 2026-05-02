using GeorgiaPlaces.Domain.Places;
using Microsoft.EntityFrameworkCore;

namespace GeorgiaPlaces.Infrastructure;

/// <summary>
/// Application DbContext. PostGIS extension is created by
/// infra/postgres/init.sql so the connecting user does not need
/// SUPERUSER inside this codebase.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Place> Places => Set<Place>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("postgis");
        modelBuilder.HasPostgresExtension("pgcrypto");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
