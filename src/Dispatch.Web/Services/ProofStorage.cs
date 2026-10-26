using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace Dispatch.Web.Services;

public interface IProofStorage
{
    Task<string> SaveAsync(int driverId, int stopId, string kind, Stream content, string contentType, CancellationToken ct = default);

    /// <summary>A short-lived link a browser can load.</summary>
    Task<Uri?> GetReadUrlAsync(string path, TimeSpan lifetime);
}

internal static class ProofPaths
{
    public static string Build(int driverId, int stopId, string kind, string contentType)
    {
        var ext = contentType == "image/png" ? ".png" : ".jpg";
        return $"{driverId}/{stopId}-{kind}-{Guid.NewGuid():N}{ext}";
    }
}

public sealed class AzureBlobProofStorage(BlobContainerClient container) : IProofStorage
{
    public async Task<string> SaveAsync(int driverId, int stopId, string kind, Stream content, string contentType, CancellationToken ct = default)
    {
        var path = ProofPaths.Build(driverId, stopId, kind, contentType);
        await container.GetBlobClient(path).UploadAsync(content,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, ct);
        return path;
    }

    public Task<Uri?> GetReadUrlAsync(string path, TimeSpan lifetime)
    {
        var blob = container.GetBlobClient(path);
        // Requires a connection string with an account key (or a user delegation key setup)
        Uri? url = blob.CanGenerateSasUri ? blob.GenerateSasUri(BlobSasPermissions.Read, DateTimeOffset.UtcNow.Add(lifetime)) : null;
        return Task.FromResult(url);
    }
}

public sealed record LocalProofFile(string FullPath, string ContentType);

/// <summary>Development only: stores proofs under App_Data, outside wwwroot so they aren't public.</summary>
public sealed class LocalProofStorage(IWebHostEnvironment env) : IProofStorage
{
    private string Root => Path.GetFullPath(Path.Combine(env.ContentRootPath, "App_Data", "proofs"));

    public async Task<string> SaveAsync(int driverId, int stopId, string kind, Stream content, string contentType, CancellationToken ct = default)
    {
        var path = ProofPaths.Build(driverId, stopId, kind, contentType);
        var full = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var file = File.Create(full);
        await content.CopyToAsync(file, ct);
        return path;
    }

    public Task<Uri?> GetReadUrlAsync(string path, TimeSpan lifetime) =>
        Task.FromResult<Uri?>(new Uri($"/proof-files/{path}", UriKind.Relative));

    /// <summary>Resolves a stored path safely (rejects ../ tricks).</summary>
    public LocalProofFile? Resolve(string path)
    {
        var full = Path.GetFullPath(Path.Combine(Root, path));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full)) return null;
        return new LocalProofFile(full, full.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
    }
}
