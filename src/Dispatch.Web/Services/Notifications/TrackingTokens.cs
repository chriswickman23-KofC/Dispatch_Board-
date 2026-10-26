using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace Dispatch.Web.Services.Notifications;

public static class TrackingTokens
{
    /// <summary>128 random bits: unguessable tracking codes.</summary>
    public static string New() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
}
