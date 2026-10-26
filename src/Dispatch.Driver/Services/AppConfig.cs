using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dispatch.Driver.Services;

public static class AppConfig
{
#if DEBUG
    // Phones can't reach localhost: use a dev tunnel URL (devtunnel host -p 7001 --allow-anonymous)
    public const string ServerUrl = "https://your-tunnel-id-7001.use.devtunnels.ms/";
#else
    public const string ServerUrl = "https://dispatch.yourco.com/";
#endif
}

public static class Json
{
    // Must match the server: camelCase names, enums as text
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

internal sealed record ProblemDto(string? Title, string? Detail);

public sealed class ApiException(string message, System.Net.HttpStatusCode? status = null) : Exception(message)
{
    public System.Net.HttpStatusCode? Status { get; } = status;
}
