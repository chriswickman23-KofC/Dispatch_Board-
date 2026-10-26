using Dispatch.Web.Services;
using Dispatch.Web.Services.Notifications;

namespace Dispatch.Web.Endpoints;

public static class TrackingEndpoints
{
    /// <summary>Delivery photo for the public tracking page: checks the tracking code before serving anything.</summary>
    public static void MapTrackingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/track/{token}/photo", async (string token, TrackingService tracking, IProofStorage storage, IServiceProvider services) =>
        {
            var path = await tracking.GetPhotoPathAsync(token);
            if (path is null) return Results.NotFound();

            if (services.GetService<LocalProofStorage>() is { } local)
                return local.Resolve(path) is { } file ? Results.File(file.FullPath, file.ContentType) : Results.NotFound();

            var url = await storage.GetReadUrlAsync(path, TimeSpan.FromMinutes(15));
            return url is null ? Results.NotFound() : Results.Redirect(url.ToString());
        });
    }
}
