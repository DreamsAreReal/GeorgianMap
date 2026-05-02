using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using GeorgiaPlaces.Application.Places;
using Microsoft.EntityFrameworkCore;

namespace GeorgiaPlaces.Infrastructure.Places;

/// <summary>
/// Read-side query for <c>GET /api/v1/places</c> against <c>places_summary</c>
/// materialized view (TZ §12.2). Dapper-based for tight control over the
/// emitted SQL and to avoid EF Core's PostGIS query overhead in hot paths.
///
/// Sort order: <c>data_freshness_score DESC, id DESC</c>. Pagination uses
/// keyset cursor (TZ §8.0) — no OFFSET, no COUNT(*).
/// </summary>
internal sealed class PlaceReadRepository : IPlaceReadRepository
{
    private readonly AppDbContext _context;

    public PlaceReadRepository(AppDbContext context) => _context = context;

    public async Task<PlaceListResponse> ListAsync(PlaceFilter filter, CancellationToken cancellationToken)
    {
        var conn = _context.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        var sql = new StringBuilder("""
            SELECT
                id,
                name,
                category,
                lat,
                lng,
                data_freshness_score,
                attr_free,
                attr_parking,
                attr_dogs,
                attr_road,
                attr_price_gel
            FROM places_summary
            WHERE 1 = 1
            """);
        var parameters = new DynamicParameters();

        if (filter.Bbox is { } bb)
        {
            sql.AppendLine().Append("  AND geom && ST_MakeEnvelope(@MinLng, @MinLat, @MaxLng, @MaxLat, 4326)::geography");
            parameters.Add("MinLng", bb.MinLng);
            parameters.Add("MinLat", bb.MinLat);
            parameters.Add("MaxLng", bb.MaxLng);
            parameters.Add("MaxLat", bb.MaxLat);
        }
        else if (filter.NearPoint is { } np && filter.RadiusKm is { } radius)
        {
            sql.AppendLine().Append("  AND ST_DWithin(geom, ST_MakePoint(@CenterLng, @CenterLat)::geography, @RadiusM)");
            parameters.Add("CenterLng", np.Lng);
            parameters.Add("CenterLat", np.Lat);
            parameters.Add("RadiusM", radius * 1000d);
        }

        if (filter.Categories.Count > 0)
        {
            sql.AppendLine().Append("  AND category = ANY(@Categories)");
            parameters.Add("Categories", filter.Categories.ToArray());
        }

        if (filter.PriceMaxGel is { } priceMax)
        {
            sql.AppendLine().Append("  AND (attr_price_gel IS NULL OR attr_price_gel <= @PriceMax)");
            parameters.Add("PriceMax", priceMax);
        }

        if (filter.Attrs.Count > 0)
        {
            sql.AppendLine().Append("  AND attributes @> @AttrsJson::jsonb");
            parameters.Add("AttrsJson", BuildAttrsJson(filter.Attrs));
        }

        if (!string.IsNullOrEmpty(filter.Cursor)
            && OpaqueCursor.TryDecode(filter.Cursor, out double curScore, out long curId))
        {
            // Keyset condition for ORDER BY (data_freshness_score DESC, id DESC).
            sql.AppendLine().Append("""
                  AND (data_freshness_score, id) < (@CurScore, @CurId)
                """);
            parameters.Add("CurScore", curScore);
            parameters.Add("CurId", curId);
        }

        sql.AppendLine().Append("ORDER BY data_freshness_score DESC, id DESC")
           .AppendLine().Append("LIMIT @LimitPlusOne");
        parameters.Add("LimitPlusOne", filter.Limit + 1);

        var rows = (await conn.QueryAsync<PlaceRow>(
            new CommandDefinition(sql.ToString(), parameters, cancellationToken: cancellationToken))
            .ConfigureAwait(false)).ToList();

        bool hasMore = rows.Count > filter.Limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        var items = rows.Select(r => new PlaceListItemDto(
            Id: r.id,
            Name: r.name,
            Category: r.category,
            Lat: r.lat,
            Lng: r.lng,
            DataFreshnessScore: r.data_freshness_score,
            KeyAttributes: BuildKeyAttributes(r))).ToList();

        string? nextCursor = hasMore && rows.Count > 0
            ? OpaqueCursor.Encode(rows[^1].data_freshness_score, rows[^1].id)
            : null;

        return new PlaceListResponse(items, nextCursor, hasMore);
    }

    private static string BuildAttrsJson(IReadOnlyDictionary<string, string> attrs)
    {
        var dict = new Dictionary<string, object?>(attrs.Count, StringComparer.Ordinal);
        foreach (var (k, v) in attrs)
        {
            // Coerce common literals so JSONB containment matches typed values.
            dict[k] = v switch
            {
                "true" => true,
                "false" => false,
                _ when int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) => i,
                _ => v,
            };
        }
        return JsonSerializer.Serialize(dict);
    }

    private static Dictionary<string, object?> BuildKeyAttributes(PlaceRow r)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (r.attr_free is not null)       d["free"] = r.attr_free;
        if (r.attr_parking is not null)    d["parking"] = r.attr_parking;
        if (r.attr_dogs is not null)       d["dogs"] = r.attr_dogs;
        if (r.attr_road is not null)       d["road"] = r.attr_road;
        if (r.attr_price_gel is not null)  d["price_gel"] = r.attr_price_gel;
        return d;
    }

#pragma warning disable IDE1006 // Naming Styles — column names match snake_case.
    private sealed class PlaceRow
    {
        public long id { get; set; }
        public string name { get; set; } = "";
        public string category { get; set; } = "";
        public double lat { get; set; }
        public double lng { get; set; }
        public double data_freshness_score { get; set; }
        public bool? attr_free { get; set; }
        public bool? attr_parking { get; set; }
        public string? attr_dogs { get; set; }
        public string? attr_road { get; set; }
        public int? attr_price_gel { get; set; }
    }
#pragma warning restore IDE1006
}
