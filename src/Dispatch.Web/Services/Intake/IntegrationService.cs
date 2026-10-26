using System.Security.Cryptography;
using System.Text;
using Dispatch.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Services.Intake;

public sealed record IntakeLogVm(DateTimeOffset ReceivedAt, string ExternalId, string Outcome, string? Detail, string? JobRef);

public sealed record IntegrationVm(
    int Id, Guid PublicId, IntegrationKind Kind, bool Active, string? ApiKeyPrefix, string? ShopDomain,
    OrderFilter Filter, string? FilterValue, TimeOnly SameDayCutoff, TimeOnly? WindowStart, TimeOnly? WindowEnd,
    int? ZoneId, DateTimeOffset? LastOrderAt, string? LastError, IReadOnlyList<IntakeLogVm> Recent);

public sealed class IntegrationForm
{
    public int? Id { get; set; }
    public IntegrationKind Kind { get; set; }
    public string? ShopDomain { get; set; }
    public string? NewSecret { get; set; }   // blank keeps the existing secret
    public OrderFilter Filter { get; set; } = OrderFilter.AllWithShippingAddress;
    public string? FilterValue { get; set; }
    public TimeOnly SameDayCutoff { get; set; } = new(14, 0);
    public TimeOnly? WindowStart { get; set; }
    public TimeOnly? WindowEnd { get; set; }
    public int? ZoneId { get; set; }
}

public sealed class IntegrationService(IDbContextFactory<ApplicationDbContext> dbFactory, IDataProtectionProvider protection)
{
    private const string KeyPrefix = "dk_";
    private readonly IDataProtector _secrets = protection.CreateProtector("Dispatch.Integrations.WebhookSecret");

    // ---------- Reading ----------

    public async Task<List<IntegrationVm>> ListAsync(int clientId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var integrations = await db.ClientIntegrations.AsNoTracking()
            .Where(i => i.ClientId == clientId)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync();

        var ids = integrations.Select(i => i.Id).ToList();
        var logs = await (
            from l in db.IntakeLogs
            where ids.Contains(l.IntegrationId)
            orderby l.ReceivedAt descending
            select new { l.IntegrationId, l.ReceivedAt, l.ExternalId, l.Outcome, l.Detail, JobRef = db.Jobs.Where(j => j.Id == l.JobId).Select(j => j.Ref).FirstOrDefault() })
            .Take(200)
            .ToListAsync();

        return integrations.Select(i => new IntegrationVm(
            i.Id, i.PublicId, i.Kind, i.Active, i.ApiKeyPrefix, i.ShopDomain, i.Filter, i.FilterValue,
            i.SameDayCutoff, i.WindowStart, i.WindowEnd, i.ZoneId, i.LastOrderAt, i.LastError,
            logs.Where(l => l.IntegrationId == i.Id).Take(10)
                .Select(l => new IntakeLogVm(l.ReceivedAt, l.ExternalId, l.Outcome, l.Detail, l.JobRef)).ToList()))
            .ToList();
    }

    // ---------- Creating and editing ----------

    /// <summary>Creates an Order API integration. The returned key is shown once and never stored in plain text.</summary>
    public async Task<(int Id, string ApiKey)> CreateApiKeyAsync(int clientId)
    {
        var (key, hash, prefix) = NewApiKey();
        await using var db = await dbFactory.CreateDbContextAsync();
        var integration = new ClientIntegration { ClientId = clientId, Kind = IntegrationKind.Api, ApiKeyHash = hash, ApiKeyPrefix = prefix };
        db.ClientIntegrations.Add(integration);
        await db.SaveChangesAsync();
        return (integration.Id, key);
    }

    /// <summary>Issues a new key; the old one stops working immediately.</summary>
    public async Task<string?> RegenerateApiKeyAsync(int integrationId)
    {
        var (key, hash, prefix) = NewApiKey();
        await using var db = await dbFactory.CreateDbContextAsync();
        var updated = await db.ClientIntegrations
            .Where(i => i.Id == integrationId && i.Kind == IntegrationKind.Api)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ApiKeyHash, hash).SetProperty(i => i.ApiKeyPrefix, prefix));
        return updated == 0 ? null : key;
    }

    public async Task<Result<string>> SaveAsync(int clientId, IntegrationForm form)
    {
        if (form.Filter != OrderFilter.AllWithShippingAddress && string.IsNullOrWhiteSpace(form.FilterValue))
            return Result<string>.Fail("Enter the shipping method or tag to match.");
        if (form.WindowStart is { } ws && form.WindowEnd is { } we && we <= ws)
            return Result<string>.Fail("The window end must be after the start.");
        if ((form.WindowStart is null) != (form.WindowEnd is null))
            return Result<string>.Fail("Enter both window times, or neither.");

        string? domain = null;
        if (form.Kind == IntegrationKind.Shopify)
        {
            domain = NormalizeShopDomain(form.ShopDomain);
            if (domain is null) return Result<string>.Fail("Enter the store's address, like yourstore.myshopify.com.");
            if (form.Id is null && string.IsNullOrWhiteSpace(form.NewSecret))
                return Result<string>.Fail("Paste the webhook signing secret from Shopify.");
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        ClientIntegration? integration;
        if (form.Id is int id)
        {
            integration = await db.ClientIntegrations.FirstOrDefaultAsync(i => i.Id == id && i.ClientId == clientId);
            if (integration is null) return Result<string>.Fail("Integration not found.");
        }
        else
        {
            integration = new ClientIntegration { ClientId = clientId, Kind = form.Kind };
            db.ClientIntegrations.Add(integration);
        }

        if (integration.Kind == IntegrationKind.Shopify)
        {
            integration.ShopDomain = domain;
            if (!string.IsNullOrWhiteSpace(form.NewSecret))
                integration.ProtectedSecret = _secrets.Protect(form.NewSecret.Trim());
        }
        integration.Filter = form.Filter;
        integration.FilterValue = string.IsNullOrWhiteSpace(form.FilterValue) ? null : form.FilterValue.Trim();
        integration.SameDayCutoff = form.SameDayCutoff;
        integration.WindowStart = form.WindowStart;
        integration.WindowEnd = form.WindowEnd;
        integration.ZoneId = form.ZoneId;

        await db.SaveChangesAsync();
        return Result<string>.Ok("Integration saved.");
    }

    public async Task SetActiveAsync(int integrationId, bool active)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.ClientIntegrations.Where(i => i.Id == integrationId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Active, active));
    }

    // ---------- Used by the intake endpoints ----------

    public async Task<ClientIntegration?> FindByApiKeyAsync(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return null;
        var hash = Hash(key.Trim());
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ClientIntegrations.AsNoTracking()
            .Include(i => i.Client)
            .FirstOrDefaultAsync(i => i.Kind == IntegrationKind.Api && i.ApiKeyHash == hash && i.Active && i.Client.Active);
    }

    public async Task<ClientIntegration?> FindShopifyAsync(Guid publicId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ClientIntegrations.AsNoTracking()
            .Include(i => i.Client)
            .FirstOrDefaultAsync(i => i.PublicId == publicId && i.Kind == IntegrationKind.Shopify && i.Active && i.Client.Active);
    }

    public string? UnprotectSecret(ClientIntegration integration)
    {
        if (integration.ProtectedSecret is null) return null;
        try { return _secrets.Unprotect(integration.ProtectedSecret); }
        catch (CryptographicException) { return null; }   // keys lost or rotated: re-enter the secret
    }

    // ---------- Helpers ----------

    private static (string Key, string Hash, string Prefix) NewApiKey()
    {
        var key = KeyPrefix + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        return (key, Hash(key), key[..10]);
    }

    // A plain SHA-256 is appropriate here: keys are long random strings, not human passwords
    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static string? NormalizeShopDomain(string? input)
    {
        var d = input?.Trim().ToLowerInvariant().Replace("https://", "").Replace("http://", "").TrimEnd('/');
        return d is not null && d.EndsWith(".myshopify.com", StringComparison.Ordinal) && d.Length > ".myshopify.com".Length ? d : null;
    }
}
