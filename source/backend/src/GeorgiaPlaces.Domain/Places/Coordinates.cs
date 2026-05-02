namespace GeorgiaPlaces.Domain.Places;

/// <summary>
/// WGS84 geographic point. Latitude in [-90, 90], longitude in [-180, 180].
/// Georgia bbox roughly: lat 41-44, lng 39-47 — broader range allowed for
/// data from sources that occasionally include nearby points.
/// </summary>
public readonly record struct Coordinates
{
    public double Latitude { get; }
    public double Longitude { get; }

    public Coordinates(double latitude, double longitude)
    {
        if (double.IsNaN(latitude) || double.IsInfinity(latitude) || latitude is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), latitude, "Latitude must be in [-90, 90].");
        }
        if (double.IsNaN(longitude) || double.IsInfinity(longitude) || longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude), longitude, "Longitude must be in [-180, 180].");
        }
        Latitude = latitude;
        Longitude = longitude;
    }
}
