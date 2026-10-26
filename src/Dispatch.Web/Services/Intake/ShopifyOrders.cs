using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dispatch.Web.Data;

namespace Dispatch.Web.Services.Intake;

public sealed record IncomingOrder(
    string ExternalId,
    string DisplayRef,
    string? RecipientName,
    string? RecipientPhone,
    string DropoffAddress,
    double? DropoffLat,
    double? DropoffLng,
    string? Notes,
    string? PickupAddress = null,
    double? PickupLat = null,
    double? PickupLng = null,
    DateOnly? DeliveryDate = null,
    TimeOnly? WindowStart = null,
    TimeOnly? WindowEnd = null,
    bool Asap = false);

public static class ShopifyOrders
{
    private static readonly string[] PaidStatuses = ["paid", "partially_paid", "authorized"];

    /// <summary>Checks the X-Shopify-Hmac-SHA256 header against the raw request body.</summary>
    public static bool VerifySignature(byte[] body, string? signatureHeader, string secret)
    {
        if (string.IsNullOrEmpty(signatureHeader)) return false;
        byte[] provided;
        try { provided = Convert.FromBase64String(signatureHeader); }
        catch (FormatException) { return false; }

        var computed = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(computed, provided);
    }

    public static string ExternalId(JsonElement order) =>
        $"shopify:{(order.TryGetProperty("id", out var id) ? id.GetRawText() : "unknown")}";

    /// <summary>Returns the order, or null with the reason it was skipped.</summary>
    public static (IncomingOrder? Order, string? SkipReason) Map(JsonElement order, OrderFilter filter, string? filterValue)
    {
        if (order.TryGetProperty("cancelled_at", out var cancelled) && cancelled.ValueKind != JsonValueKind.Null)
            return (null, "Order is cancelled");

        var financial = Str(order, "financial_status");
        if (financial is null || !PaidStatuses.Contains(financial))
            return (null, $"Not paid yet ({financial ?? "unknown"})");

        if (!order.TryGetProperty("shipping_address", out var addr) || addr.ValueKind != JsonValueKind.Object)
            return (null, "No shipping address (pickup or digital order)");

        switch (filter)
        {
            case OrderFilter.ShippingMethodContains:
                var methods = order.TryGetProperty("shipping_lines", out var lines) && lines.ValueKind == JsonValueKind.Array
                    ? lines.EnumerateArray().Select(l => Str(l, "title") ?? "").ToList()
                    : [];
                if (!methods.Any(m => m.Contains(filterValue ?? "", StringComparison.OrdinalIgnoreCase)))
                    return (null, $"Shipping method isn't \"{filterValue}\" ({string.Join(", ", methods)})");
                break;

            case OrderFilter.HasTag:
                var tags = (Str(order, "tags") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (!tags.Contains(filterValue ?? "", StringComparer.OrdinalIgnoreCase))
                    return (null, $"Doesn't have the tag \"{filterValue}\"");
                break;
        }

        var street = string.Join(", ", new[] { Str(addr, "address1"), Str(addr, "address2") }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var cityLine = string.Join(" ", new[] { Str(addr, "province_code"), Str(addr, "zip") }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var address = string.Join(", ", new[] { street, Str(addr, "city"), cityLine }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(street)) return (null, "Shipping address has no street");

        var name = Str(addr, "name")
                   ?? string.Join(" ", new[] { Str(addr, "first_name"), Str(addr, "last_name") }.Where(s => s is not null));
        var phone = Str(addr, "phone") ?? Str(order, "phone")
                    ?? (order.TryGetProperty("customer", out var customer) && customer.ValueKind == JsonValueKind.Object ? Str(customer, "phone") : null);

        return (new IncomingOrder(
            ExternalId(order),
            DisplayRef: Str(order, "name") ?? "Shopify order",
            RecipientName: string.IsNullOrWhiteSpace(name) ? null : name,
            RecipientPhone: phone,
            DropoffAddress: address,
            DropoffLat: Num(addr, "latitude"),
            DropoffLng: Num(addr, "longitude"),
            Notes: Str(order, "note")), null);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
