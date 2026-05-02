using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GeorgiaPlaces.Infrastructure.Migrations;

/// <summary>
/// First schema migration: <c>places</c> table per TZ §4.1, with
/// <c>hidden</c> columns and partial indexes excluding hidden rows.
/// PostGIS / pgcrypto extensions are pre-created by
/// <c>infra/postgres/init.sql</c> — this migration just declares
/// them so EF's model snapshot is aware.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260502_000_InitialPlaces")]
public sealed partial class InitialPlaces : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterDatabase()
            .Annotation("Npgsql:PostgresExtension:pgcrypto", ",,")
            .Annotation("Npgsql:PostgresExtension:postgis", ",,");

        migrationBuilder.Sql("""
            CREATE TABLE places (
                id                    BIGSERIAL PRIMARY KEY,
                name                  TEXT NOT NULL,
                name_en               TEXT,
                name_ka               TEXT,
                description           TEXT,
                geom                  GEOGRAPHY(POINT, 4326) NOT NULL,
                category              TEXT NOT NULL,
                subcategory           TEXT,
                osm_id                TEXT,
                osm_type              TEXT,
                google_place_id       TEXT,
                wikidata_id           TEXT,
                attributes            JSONB NOT NULL DEFAULT '{}'::jsonb,
                data_freshness_score  REAL NOT NULL DEFAULT 0.5,
                last_verified_at      TIMESTAMPTZ,
                hidden                BOOLEAN NOT NULL DEFAULT FALSE,
                hidden_reason         TEXT,
                hidden_at             TIMESTAMPTZ,
                created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at            TIMESTAMPTZ NOT NULL DEFAULT now()
            );

            -- Partial indexes per TZ §4.1: exclude hidden from hot-path scans.
            CREATE INDEX idx_places_geom        ON places USING GIST (geom)            WHERE hidden = false;
            CREATE INDEX idx_places_attributes  ON places USING GIN  (attributes)      WHERE hidden = false;
            CREATE INDEX idx_places_category    ON places (category)                   WHERE hidden = false;

            -- Dedup-helpers for parser merges.
            CREATE INDEX idx_places_google_id   ON places (google_place_id)            WHERE google_place_id IS NOT NULL;
            CREATE INDEX idx_places_osm_id      ON places (osm_id, osm_type)           WHERE osm_id IS NOT NULL;

            -- Full id index for recompute jobs that scan hidden rows too
            -- (P1 from 2nd review: partial-only indexes force seq scan).
            CREATE INDEX idx_places_id_all      ON places (id);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS places CASCADE;");
    }
}
