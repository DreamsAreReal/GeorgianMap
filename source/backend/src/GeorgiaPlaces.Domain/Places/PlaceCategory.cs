namespace GeorgiaPlaces.Domain.Places;

/// <summary>
/// Top-level category of a place. Stored as text in DB (TZ §4.1) so the
/// vocabulary can grow without schema migrations. Domain validates
/// against the known set.
/// </summary>
public readonly record struct PlaceCategory
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "viewpoint", "monastery", "waterfall", "restaurant",
        "lake", "mountain", "beach", "fortress", "cave",
        "thermal_spring", "winery", "ski_resort", "park",
        "aquapark", "museum", "market", "gas_station",
    };

    public string Value { get; }

    private PlaceCategory(string value) => Value = value;

    public static PlaceCategory From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Known.Contains(value))
        {
            throw new ArgumentException($"Unknown category '{value}'.", nameof(value));
        }
        return new PlaceCategory(value);
    }

    public static bool IsKnown(string value) => !string.IsNullOrEmpty(value) && Known.Contains(value);

    public override string ToString() => Value;
}
