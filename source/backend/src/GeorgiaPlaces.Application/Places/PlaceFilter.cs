namespace GeorgiaPlaces.Application.Places;

/// <summary>
/// Parsed and validated filter for <c>GET /api/v1/places</c> (TZ §8.1).
/// All ranges are inclusive. Validation happens in <see cref="PlaceFilterParser"/>.
/// </summary>
public sealed record PlaceFilter
{
    /// <summary>Bounding box: (minLat, minLng, maxLat, maxLng). Mutually exclusive with <see cref="NearPoint"/>.</summary>
    public BoundingBox? Bbox { get; init; }

    /// <summary>Centre point for radius search, in degrees.</summary>
    public (double Lat, double Lng)? NearPoint { get; init; }

    /// <summary>Radius in kilometres. Required when <see cref="NearPoint"/> is set.</summary>
    public double? RadiusKm { get; init; }

    /// <summary>List of categories. OR-semantics — match any.</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>Attribute filter. AND-semantics — all must match.</summary>
    public IReadOnlyDictionary<string, string> Attrs { get; init; } = new Dictionary<string, string>(0);

    /// <summary>Maximum price (inclusive) in GEL. Null = no filter.</summary>
    public int? PriceMaxGel { get; init; }

    /// <summary>Page size (1..100).</summary>
    public int Limit { get; init; } = 50;

    /// <summary>Opaque cursor from previous page; null for first page.</summary>
    public string? Cursor { get; init; }
}

public readonly record struct BoundingBox(double MinLat, double MinLng, double MaxLat, double MaxLng);
