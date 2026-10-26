using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dispatch.Shared;

namespace Dispatch.Driver.Services;

public sealed class AuthService(TokenStore store, IHttpClientFactory httpFactory)
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>Raised when the server rejects the refresh token (password changed, deactivated, 30 days idle).</summary>
    public event Action? SignedOut;

    public async Task<bool> IsSignedInAsync() => await store.GetRefreshTokenAsync() is not null;

    /// <summary>Returns null on success, or a message to show the driver.</summary>
    public async Task<string?> LoginAsync(string email, string password)
    {
        try
        {
            var http = httpFactory.CreateClient("auth");
            using var res = await http.PostAsJsonAsync("api/driver/auth/login", new DriverLoginRequest(email, password), Json.Options);
            if (!res.IsSuccessStatusCode)
            {
                var problem = await TryReadProblemAsync(res);
                return problem ?? "Sign-in failed. Please try again.";
            }
            var tokens = await res.Content.ReadFromJsonAsync<TokenResponse>(Json.Options);
            await store.SaveAsync(tokens!);
            return null;
        }
        catch (HttpRequestException)
        {
            return "Can't reach the server. Check your connection.";
        }
    }

    /// <summary>Returns a usable access token, refreshing it shortly before it expires.</summary>
    public async Task<string?> GetValidAccessTokenAsync(bool forceRefresh = false)
    {
        var token = await store.GetAccessTokenAsync();
        if (!forceRefresh && token is not null && await store.GetExpiryAsync() > DateTimeOffset.UtcNow.AddMinutes(2))
            return token;

        await _refreshLock.WaitAsync();
        try
        {
            // Another request may have refreshed while this one waited
            token = await store.GetAccessTokenAsync();
            if (!forceRefresh && token is not null && await store.GetExpiryAsync() > DateTimeOffset.UtcNow.AddMinutes(2))
                return token;

            var refresh = await store.GetRefreshTokenAsync();
            if (refresh is null) return null;

            var http = httpFactory.CreateClient("auth");
            using var res = await http.PostAsJsonAsync("api/driver/auth/refresh", new TokenRefreshRequest(refresh), Json.Options);
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                store.Clear();
                SignedOut?.Invoke();
                return null;
            }
            if (!res.IsSuccessStatusCode) return token;   // server hiccup: keep the old token, try again later

            var tokens = await res.Content.ReadFromJsonAsync<TokenResponse>(Json.Options);
            await store.SaveAsync(tokens!);
            return tokens!.AccessToken;
        }
        catch (HttpRequestException)
        {
            return token;   // offline: keep going with what we have
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void SignOut()
    {
        store.Clear();
        SignedOut?.Invoke();
    }

    internal static async Task<string?> TryReadProblemAsync(HttpResponseMessage res)
    {
        try { return (await res.Content.ReadFromJsonAsync<ProblemDto>(Json.Options))?.Detail; }
        catch (JsonException) { return null; }
    }
}

/// <summary>Adds the bearer token to every API call.</summary>
public sealed class AuthHandler(AuthService auth) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await auth.GetValidAccessTokenAsync();
        if (token is not null) request.Headers.Authorization = new("Bearer", token);

        var response = await base.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && token is not null)
            await auth.GetValidAccessTokenAsync(forceRefresh: true);   // the next call will succeed
        return response;
    }
}
