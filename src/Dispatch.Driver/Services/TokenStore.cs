using Dispatch.Shared;

namespace Dispatch.Driver.Services;

/// <summary>Tokens live in the phone's secure storage (Keychain / Keystore), not in plain files.</summary>
public sealed class TokenStore
{
    private const string AccessKey = "access_token";
    private const string RefreshKey = "refresh_token";
    private const string ExpiresKey = "access_expires";

    public async Task SaveAsync(TokenResponse tokens)
    {
        await SecureStorage.Default.SetAsync(AccessKey, tokens.AccessToken);
        await SecureStorage.Default.SetAsync(RefreshKey, tokens.RefreshToken);
        var expires = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn).ToUnixTimeSeconds();
        await SecureStorage.Default.SetAsync(ExpiresKey, expires.ToString());
    }

    public Task<string?> GetAccessTokenAsync() => SecureStorage.Default.GetAsync(AccessKey);
    public Task<string?> GetRefreshTokenAsync() => SecureStorage.Default.GetAsync(RefreshKey);

    public async Task<DateTimeOffset?> GetExpiryAsync() =>
        long.TryParse(await SecureStorage.Default.GetAsync(ExpiresKey), out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;

    public void Clear()
    {
        SecureStorage.Default.Remove(AccessKey);
        SecureStorage.Default.Remove(RefreshKey);
        SecureStorage.Default.Remove(ExpiresKey);
    }
}
