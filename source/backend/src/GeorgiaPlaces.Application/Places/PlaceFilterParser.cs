using System.Globalization;
using GeorgiaPlaces.Domain.Places;

namespace GeorgiaPlaces.Application.Places;

/// <summary>
/// Parses raw query string parameters from <c>GET /api/v1/places</c> into a
/// validated <see cref="PlaceFilter"/>. Errors return a <see cref="FilterError"/>
/// suitable for emitting RFC 7807 ProblemDetails (TZ §8.0).
/// </summary>
public static class PlaceFilterParser
{
    // 500 is enough to fully render Georgia at country zoom on a map view.
    // Beyond that, clients use cursor pagination (TZ §8.0).
    public const int MaxLimit = 500;
    public const int MaxAttrPairs = 10;
    public const int MaxCategories = 20;

    public static FilterParseResult Parse(
        string? bbox,
        string? near,
        string? radiusKm,
        string? category,
        string? attrs,
        string? priceMax,
        string? limit,
        string? cursor)
    {
        var filter = new PlaceFilter();

        // ---- bbox: "minLat,minLng,maxLat,maxLng" ----
        if (!string.IsNullOrWhiteSpace(bbox))
        {
            var parts = bbox.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 4
                || !TryDouble(parts[0], out double minLat)
                || !TryDouble(parts[1], out double minLng)
                || !TryDouble(parts[2], out double maxLat)
                || !TryDouble(parts[3], out double maxLng))
            {
                return FilterParseResult.Fail("bbox", "bbox must be 'minLat,minLng,maxLat,maxLng' as four decimals.");
            }
            if (minLat > maxLat || minLng > maxLng)
            {
                return FilterParseResult.Fail("bbox", "bbox: min must be <= max for both lat and lng.");
            }
            try
            {
                _ = new Coordinates(minLat, minLng);
                _ = new Coordinates(maxLat, maxLng);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return FilterParseResult.Fail("bbox", ex.Message);
            }
            filter = filter with { Bbox = new BoundingBox(minLat, minLng, maxLat, maxLng) };
        }

        // ---- near + radius ----
        if (!string.IsNullOrWhiteSpace(near))
        {
            var parts = near.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !TryDouble(parts[0], out double lat) || !TryDouble(parts[1], out double lng))
            {
                return FilterParseResult.Fail("near", "near must be 'lat,lng' as two decimals.");
            }
            try { _ = new Coordinates(lat, lng); }
            catch (ArgumentOutOfRangeException ex) { return FilterParseResult.Fail("near", ex.Message); }

            if (string.IsNullOrWhiteSpace(radiusKm) || !TryDouble(radiusKm, out double r) || r <= 0 || r > 500)
            {
                return FilterParseResult.Fail("radius_km", "radius_km must be a positive number <= 500.");
            }
            filter = filter with { NearPoint = (lat, lng), RadiusKm = r };
        }

        if (filter.Bbox is not null && filter.NearPoint is not null)
        {
            return FilterParseResult.Fail("bbox", "bbox and near are mutually exclusive — pick one.");
        }

        // ---- category: comma-separated ----
        if (!string.IsNullOrWhiteSpace(category))
        {
            var cats = category
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
            if (cats.Length > MaxCategories)
            {
                return FilterParseResult.Fail("category", $"At most {MaxCategories} categories allowed.");
            }
            foreach (var c in cats)
            {
                if (!PlaceCategory.IsKnown(c))
                {
                    return FilterParseResult.Fail("category", $"Unknown category '{c}'.");
                }
            }
            filter = filter with { Categories = cats };
        }

        // ---- attrs: "key:value,key:value" ----
        if (!string.IsNullOrWhiteSpace(attrs))
        {
            var pairs = attrs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (pairs.Length > MaxAttrPairs)
            {
                return FilterParseResult.Fail("attrs", $"At most {MaxAttrPairs} attribute filters allowed.");
            }
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in pairs)
            {
                int colon = pair.IndexOf(':');
                if (colon <= 0 || colon == pair.Length - 1)
                {
                    return FilterParseResult.Fail("attrs", $"attrs entry '{pair}' must be 'key:value'.");
                }
                string key = pair[..colon].Trim();
                string val = pair[(colon + 1)..].Trim();
                if (key.Length == 0 || val.Length == 0)
                {
                    return FilterParseResult.Fail("attrs", $"attrs entry '{pair}' has empty key or value.");
                }
                dict[key] = val;
            }
            filter = filter with { Attrs = dict };
        }

        // ---- price_max ----
        if (!string.IsNullOrWhiteSpace(priceMax))
        {
            if (!int.TryParse(priceMax, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) || p < 0)
            {
                return FilterParseResult.Fail("price_max", "price_max must be a non-negative integer.");
            }
            filter = filter with { PriceMaxGel = p };
        }

        // ---- limit ----
        if (!string.IsNullOrWhiteSpace(limit))
        {
            if (!int.TryParse(limit, NumberStyles.Integer, CultureInfo.InvariantCulture, out int l) || l < 1 || l > MaxLimit)
            {
                return FilterParseResult.Fail("limit", $"limit must be 1..{MaxLimit}.");
            }
            filter = filter with { Limit = l };
        }

        // ---- cursor (validated when decoded by repository) ----
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            filter = filter with { Cursor = cursor };
        }

        return FilterParseResult.Ok(filter);
    }

    private static bool TryDouble(string s, out double value) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

public readonly record struct FilterError(string Field, string Message);

public readonly record struct FilterParseResult(PlaceFilter? Value, FilterError? Error)
{
    public bool IsSuccess => Error is null;

    public static FilterParseResult Ok(PlaceFilter f) => new(f, null);
    public static FilterParseResult Fail(string field, string message) => new(null, new FilterError(field, message));
}
