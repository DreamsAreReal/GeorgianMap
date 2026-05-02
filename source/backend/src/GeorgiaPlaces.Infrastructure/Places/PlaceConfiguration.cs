using System.Text.Json;
using GeorgiaPlaces.Domain.Places;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NetTopologySuite.Geometries;

namespace GeorgiaPlaces.Infrastructure.Places;

internal sealed class PlaceConfiguration : IEntityTypeConfiguration<Place>
{
    public void Configure(EntityTypeBuilder<Place> builder)
    {
        builder.ToTable("places");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new PlaceId(value))
            .ValueGeneratedOnAdd();

        builder.Property(p => p.Name).HasColumnName("name").IsRequired();
        builder.Property(p => p.NameEn).HasColumnName("name_en");
        builder.Property(p => p.NameKa).HasColumnName("name_ka");
        builder.Property(p => p.Description).HasColumnName("description");

        // Location is a value-object exposed by the domain; the persisted form is a
        // PostGIS GEOGRAPHY shadow property. Mapping between them happens in the
        // command-side repository (write path) — read-side uses Dapper directly.
        builder.Ignore(p => p.Location);
        builder.Property<Point>("Geom")
            .HasColumnName("geom")
            .HasColumnType("geography(Point, 4326)")
            .IsRequired();

        builder.Property(p => p.Category)
            .HasColumnName("category")
            .HasConversion(c => c.Value, v => PlaceCategory.From(v))
            .IsRequired();
        builder.Property(p => p.Subcategory).HasColumnName("subcategory");

        builder.Property(p => p.OsmId).HasColumnName("osm_id");
        builder.Property(p => p.OsmType).HasColumnName("osm_type");
        builder.Property(p => p.GooglePlaceId).HasColumnName("google_place_id");
        builder.Property(p => p.WikidataId).HasColumnName("wikidata_id");

        var attrComparer = new ValueComparer<IReadOnlyDictionary<string, object?>>(
            (a, b) => a == b
                || (a != null && b != null
                    && a.Count == b.Count
                    && JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions)),
            v => v == null ? 0 : JsonSerializer.Serialize(v, JsonOptions).GetHashCode(StringComparison.Ordinal),
            v => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(v));

        builder.Property(p => p.Attributes)
            .HasColumnName("attributes")
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonOptions),
                v => JsonSerializer.Deserialize<Dictionary<string, object?>>(v, JsonOptions)!
                     ?? new Dictionary<string, object?>(0),
                attrComparer)
            .HasDefaultValueSql("'{}'::jsonb");

        builder.Property(p => p.DataFreshnessScore)
            .HasColumnName("data_freshness_score")
            .HasDefaultValue(0.5);
        builder.Property(p => p.LastVerifiedAt).HasColumnName("last_verified_at");

        builder.Property(p => p.Hidden).HasColumnName("hidden").HasDefaultValue(false);
        builder.Property(p => p.HiddenReason).HasColumnName("hidden_reason");
        builder.Property(p => p.HiddenAt).HasColumnName("hidden_at");

        builder.Property(p => p.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");

        // Partial indexes per TZ §4.1 — exclude hidden rows from hot-path scans.
        builder.HasIndex("Geom")
            .HasDatabaseName("idx_places_geom")
            .HasMethod("gist")
            .HasFilter("hidden = false");
        builder.HasIndex(p => p.Category)
            .HasDatabaseName("idx_places_category")
            .HasFilter("hidden = false");
        builder.HasIndex(p => p.GooglePlaceId)
            .HasDatabaseName("idx_places_google_id")
            .HasFilter("google_place_id IS NOT NULL");
        builder.HasIndex(p => new { p.OsmId, p.OsmType })
            .HasDatabaseName("idx_places_osm_id")
            .HasFilter("osm_id IS NOT NULL");

        // Full btree on id for recompute jobs (P1 from 2nd review: partial-only
        // indexes force seq scans on hidden rows).
        builder.HasIndex(p => p.Id).HasDatabaseName("idx_places_id_all");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
    };
}
