namespace GeorgiaPlaces.Application.Places;

public interface IPlaceReadRepository
{
    Task<PlaceListResponse> ListAsync(PlaceFilter filter, CancellationToken cancellationToken);
}
