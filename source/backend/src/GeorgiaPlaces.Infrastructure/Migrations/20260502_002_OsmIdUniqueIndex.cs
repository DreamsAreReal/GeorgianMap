using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeorgiaPlaces.Infrastructure.Migrations;

/// <summary>
/// Replaces the non-unique <c>idx_places_osm_id</c> with a UNIQUE partial index.
/// Required by the OSM parser's <c>ON CONFLICT (osm_id, osm_type)</c> upsert —
/// without UNIQUE, Postgres cannot use the index as a conflict target.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260502_002_OsmIdUniqueIndex")]
public sealed partial class OsmIdUniqueIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS idx_places_osm_id;
            CREATE UNIQUE INDEX idx_places_osm_id
                ON places (osm_id, osm_type)
                WHERE osm_id IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS idx_places_osm_id;
            CREATE INDEX idx_places_osm_id
                ON places (osm_id, osm_type)
                WHERE osm_id IS NOT NULL;
            """);
    }
}
