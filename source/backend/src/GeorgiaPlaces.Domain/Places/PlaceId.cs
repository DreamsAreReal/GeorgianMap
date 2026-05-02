namespace GeorgiaPlaces.Domain.Places;

/// <summary>
/// Strongly-typed identifier for <see cref="Place"/>. Wraps long (BIGSERIAL in DB).
/// </summary>
public readonly record struct PlaceId(long Value)
{
    public static PlaceId From(long value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "PlaceId must be positive.");
        }
        return new PlaceId(value);
    }

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
