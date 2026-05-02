using GeorgiaPlaces.Application.Places;
using Microsoft.AspNetCore.Mvc;

namespace GeorgiaPlaces.Api.Endpoints;

public static class PlacesEndpoints
{
    public static IEndpointRouteBuilder MapPlacesEndpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1/places").WithTags("places");

        v1.MapGet("", ListPlacesAsync).WithName("ListPlaces");

        return app;
    }

    private static async Task<IResult> ListPlacesAsync(
        HttpContext http,
        IPlaceReadRepository repo,
        [FromQuery(Name = "bbox")] string? bbox,
        [FromQuery(Name = "near")] string? near,
        [FromQuery(Name = "radius_km")] string? radiusKm,
        [FromQuery(Name = "category")] string? category,
        [FromQuery(Name = "attrs")] string? attrs,
        [FromQuery(Name = "price_max")] string? priceMax,
        [FromQuery(Name = "limit")] string? limit,
        [FromQuery(Name = "cursor")] string? cursor,
        CancellationToken ct)
    {
        var parsed = PlaceFilterParser.Parse(bbox, near, radiusKm, category, attrs, priceMax, limit, cursor);
        if (!parsed.IsSuccess)
        {
            var err = parsed.Error!.Value;
            return Results.Problem(
                type: "https://georgia-places.example/problems/invalid-filter",
                title: "Invalid filter parameter.",
                detail: err.Message,
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["field"] = err.Field });
        }

        PlaceListResponse response = await repo.ListAsync(parsed.Value!, ct).ConfigureAwait(false);

        // Cache headers per TZ §8.1: short browser TTL, long CDN TTL.
        // CDN can be invalidated by parser when places change (out-of-band).
        http.Response.Headers.CacheControl = "public, max-age=300, s-maxage=3600";
        http.Response.Headers.Vary = "Accept-Encoding";

        return Results.Ok(response);
    }
}
