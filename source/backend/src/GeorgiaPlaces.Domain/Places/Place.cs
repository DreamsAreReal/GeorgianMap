namespace GeorgiaPlaces.Domain.Places;

/// <summary>
/// Aggregate root for a place. Mirrors `places` table per TZ §4.1.
/// Mutating operations enforce domain invariants (hidden flag,
/// freshness score range, etc.) and produce update timestamps.
/// </summary>
public sealed class Place
{
    public PlaceId Id { get; private set; }
    public string Name { get; private set; }
    public string? NameEn { get; private set; }
    public string? NameKa { get; private set; }
    public string? Description { get; private set; }

    public Coordinates Location { get; private set; }
    public PlaceCategory Category { get; private set; }
    public string? Subcategory { get; private set; }

    public string? OsmId { get; private set; }
    public string? OsmType { get; private set; }
    public string? GooglePlaceId { get; private set; }
    public string? WikidataId { get; private set; }

    /// <summary>JSONB attributes (free, dogs, parking, ...). Kept opaque at domain level.</summary>
    public IReadOnlyDictionary<string, object?> Attributes { get; private set; }

    public double DataFreshnessScore { get; private set; }
    public DateTimeOffset? LastVerifiedAt { get; private set; }

    public bool Hidden { get; private set; }
    public string? HiddenReason { get; private set; }
    public DateTimeOffset? HiddenAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Place(
        PlaceId id,
        string name,
        Coordinates location,
        PlaceCategory category,
        IReadOnlyDictionary<string, object?> attributes,
        double dataFreshnessScore,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        Name = name;
        Location = location;
        Category = category;
        Attributes = attributes;
        DataFreshnessScore = dataFreshnessScore;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>EF Core requires a parameterless constructor for materialization.</summary>
    private Place()
    {
        Name = string.Empty;
        Location = default;
        Category = default;
        Attributes = new Dictionary<string, object?>(0);
    }

    public static Place Create(
        string name,
        Coordinates location,
        PlaceCategory category,
        IDictionary<string, object?>? attributes = null,
        TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var attrs = attributes is null
            ? (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(0)
            : new Dictionary<string, object?>(attributes);

        return new Place(
            id: default,                  // assigned by DB on insert
            name: name,
            location: location,
            category: category,
            attributes: attrs,
            dataFreshnessScore: 0.5,
            createdAt: now,
            updatedAt: now);
    }

    public void Hide(string reason, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (Hidden) return;
        Hidden = true;
        HiddenReason = reason;
        HiddenAt = (clock ?? TimeProvider.System).GetUtcNow();
        UpdatedAt = HiddenAt.Value;
    }

    public void Unhide(TimeProvider? clock = null)
    {
        if (!Hidden) return;
        Hidden = false;
        HiddenReason = null;
        HiddenAt = null;
        UpdatedAt = (clock ?? TimeProvider.System).GetUtcNow();
    }
}
