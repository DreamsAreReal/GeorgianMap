namespace GeorgiaPlaces.Application.Places;

/// <summary>Item returned from <c>GET /api/v1/places</c> per TZ §8.1.</summary>
public sealed record PlaceListItemDto(
    long Id,
    string Name,
    string Category,
    double Lat,
    double Lng,
    double DataFreshnessScore,
    IReadOnlyDictionary<string, object?> KeyAttributes);

public sealed record PlaceListResponse(
    IReadOnlyList<PlaceListItemDto> Places,
    string? NextCursor,
    bool HasMore);
