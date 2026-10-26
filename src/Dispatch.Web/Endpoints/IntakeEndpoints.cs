using System.Text.Json;
using Dispatch.Web.Services.Intake;

namespace Dispatch.Web.Endpoints;

public static class IntakeEndpoints
{
    private const int MaxBodyBytes = 1_000_000;

    public static void MapIntakeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/intake")
            .RequireRateLimiting("intake")
            .DisableAntiforgery();   // authenticated by API key or signature, not cookies

        group.MapPost("/orders", ApiOrderAsync);
        group.MapPost("/shopify/{publicId:guid}", ShopifyAsync);
    }

    private static async Task<IResult> ApiOrderAsync(
        HttpRequest request, ApiOrderRequest body,
        IntegrationService integrations, OrderIntakeService intake, IConfiguration config)
    {
        var auth = request.Headers.Authorization.ToString();
        var key = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : request.Headers["X-Api-Key"].ToString();

        var integration = await integrations.FindByApiKeyAsync(key);
        if (integration is null) return Results.Problem("Invalid or inactive API key.", statusCode: StatusCodes.Status401Unauthorized);

        var result = await intake.IntakeApiOrderAsync(integration, body);
        var baseUrl = (config["App:PublicBaseUrl"] ?? "").TrimEnd('/');
        var response = new
        {
            jobRef = result.JobRef,
            deliveryDate = result.DeliveryDate,
            trackingUrl = result.TrackingToken is null ? null : $"{baseUrl}/track/{result.TrackingToken}",
            duplicate = result.Outcome == IntakeOutcome.Duplicate,
            warning = result.Detail,
        };

        return result.Outcome switch
        {
            IntakeOutcome.Created => Results.Created(response.trackingUrl, response),
            IntakeOutcome.Duplicate => Results.Ok(response),
            _ => Results.Problem(result.Detail, statusCode: StatusCodes.Status400BadRequest),
        };
    }

    private static async Task<IResult> ShopifyAsync(
        Guid publicId, HttpRequest request,
        IntegrationService integrations, OrderIntakeService intake)
    {
        var integration = await integrations.FindShopifyAsync(publicId);
        if (integration is null) return Results.NotFound();
        if (request.ContentLength > MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        // The signature covers the exact bytes Shopify sent, so read the raw body first
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer);
        var body = buffer.ToArray();

        var secret = integrations.UnprotectSecret(integration);
        if (secret is null || !ShopifyOrders.VerifySignature(body, request.Headers["X-Shopify-Hmac-SHA256"], secret))
            return Results.Unauthorized();

        var shop = request.Headers["X-Shopify-Shop-Domain"].ToString();
        if (!string.Equals(shop, integration.ShopDomain, StringComparison.OrdinalIgnoreCase))
            return Results.Unauthorized();

        using var doc = JsonDocument.Parse(body);
        await intake.HandleShopifyAsync(integration, request.Headers["X-Shopify-Topic"].ToString(), doc.RootElement);

        // Always 200 once verified, so Shopify doesn't keep retrying orders we deliberately skipped.
        // Real failures (database down) throw, return 500, and Shopify retries them automatically.
        return Results.Ok();
    }
}
