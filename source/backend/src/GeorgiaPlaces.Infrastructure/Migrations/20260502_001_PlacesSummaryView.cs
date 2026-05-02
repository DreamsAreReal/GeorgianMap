using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeorgiaPlaces.Infrastructure.Migrations;

/// <summary>
/// Materialized view <c>places_summary</c> per TZ §12.2.
/// Refreshed hourly by cron via <c>CONCURRENTLY</c> (advisory_lock guards
/// against overlap) — see <c>infra/scripts/refresh-places-summary.sh</c>.
/// The unique index on <c>id</c> is required by <c>REFRESH ... CONCURRENTLY</c>.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260502_001_PlacesSummaryView")]
public sealed partial class PlacesSummaryView : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE MATERIALIZED VIEW places_summary AS
            SELECT
                p.id,
                p.name,
                p.category,
                p.geom,
                ST_Y(p.geom::geometry) AS lat,
                ST_X(p.geom::geometry) AS lng,
                (p.attributes->>'free')::boolean       AS attr_free,
                (p.attributes->>'parking')::boolean    AS attr_parking,
                p.attributes->>'dogs'                  AS attr_dogs,
                p.attributes->>'road'                  AS attr_road,
                NULLIF(p.attributes->>'price_gel', '')::int AS attr_price_gel,
                p.attributes,
                p.data_freshness_score,
                p.last_verified_at
            FROM places p
            WHERE p.hidden = false;

            CREATE UNIQUE INDEX idx_places_summary_id    ON places_summary (id);
            CREATE INDEX        idx_places_summary_geom  ON places_summary USING GIST (geom);
            CREATE INDEX        idx_places_summary_attrs ON places_summary USING GIN (attributes);
            CREATE INDEX        idx_places_summary_cat   ON places_summary (category);
            CREATE INDEX        idx_places_summary_fresh ON places_summary (data_freshness_score DESC)
                                                           WHERE data_freshness_score > 0.5;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP MATERIALIZED VIEW IF EXISTS places_summary CASCADE;");
    }
}
