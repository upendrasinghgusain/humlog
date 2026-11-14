using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Humlog.Sync;

/// <summary>
/// The website storage account's static website container ($web): the web page and catalogue.json, side by side
/// so the TV reads the catalogue from the same origin. The account is firewalled; only this function can write to it.
/// </summary>
public sealed class CatalogueStore(BlobServiceClient service)
{
    public const string Container = "$web";
    public const string Name = "catalogue.json";
    public const string PreviousName = "catalogue.prev.json";

    /// <summary>
    /// Every site file is published at the root (behind Cloudflare Access: email + PIN) and under tv/ (no login;
    /// a Cloudflare firewall rule demands the TV's secret key in the URL instead).
    /// </summary>
    private static readonly string[] Prefixes = ["", "tv/"];

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
    };

    private readonly BlobContainerClient _container = service.GetBlobContainerClient(Container);

    /// <summary>Turns on the static website and uploads any page file whose content changed. Returns the number uploaded.</summary>
    public async Task<int> PublishSiteAsync(string directory, CancellationToken ct)
    {
        var props = await service.GetPropertiesAsync(ct);
        if (props.Value.StaticWebsite is not { Enabled: true })
        {
            props.Value.StaticWebsite = new BlobStaticWebsite { Enabled = true, IndexDocument = "index.html", ErrorDocument404Path = "index.html" };
            await service.SetPropertiesAsync(props.Value, ct);
        }

        var uploaded = 0;
        foreach (var path in Directory.EnumerateFiles(directory))
        foreach (var prefix in Prefixes)
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            var blob = _container.GetBlobClient(prefix + Path.GetFileName(path));
            if (await ContentMd5Async(blob, ct) is { } existing && existing.AsSpan().SequenceEqual(MD5.HashData(bytes)))
                continue;

            await blob.UploadAsync(BinaryData.FromBytes(bytes), new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = ContentTypes.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream"),
                    CacheControl = "no-cache",
                },
            }, ct);
            uploaded++;
        }
        return uploaded;
    }

    private static async Task<byte[]?> ContentMd5Async(BlobClient blob, CancellationToken ct)
    {
        try
        {
            return (await blob.GetPropertiesAsync(cancellationToken: ct)).Value.ContentHash;
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return null;
        }
    }

    public sealed record Current(string? Hash, int? Count);

    public async Task<Current> GetCurrentAsync(CancellationToken ct)
    {
        try
        {
            var props = await _container.GetBlobClient(Name).GetPropertiesAsync(cancellationToken: ct);
            props.Value.Metadata.TryGetValue("hash", out var hash);

            // A missing copy (e.g. a newly added prefix) forces a publish even though the album is unchanged.
            foreach (var prefix in Prefixes.Where(p => p.Length > 0))
                if (!await _container.GetBlobClient(prefix + Name).ExistsAsync(ct)) hash = null;

            int? count = props.Value.Metadata.TryGetValue("count", out var c) && int.TryParse(c, out var n) ? n : null;
            return new Current(hash, count);
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return new Current(null, null);
        }
    }

    public async Task PublishAsync(Catalogue catalogue, string hash, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(Name);

        // Keep the last good version beside the new one.
        if (await blob.ExistsAsync(ct))
        {
            var previous = await blob.DownloadContentAsync(ct);
            await Upload(_container.GetBlobClient(PreviousName), previous.Value.Content, null, ct);
        }

        var bytes = BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(catalogue, CatalogueBuilder.Json));
        var metadata = new Dictionary<string, string> { ["hash"] = hash, ["count"] = catalogue.Items.Count.ToString() };

        // Copies first: the root blob's hash marks the whole publish as done, so a failed copy is retried next run.
        foreach (var prefix in Prefixes.Where(p => p.Length > 0))
            await Upload(_container.GetBlobClient(prefix + Name), bytes, null, ct);
        await Upload(blob, bytes, metadata, ct);
    }

    private static Task Upload(BlobClient blob, BinaryData content, IDictionary<string, string>? metadata, CancellationToken ct) =>
        blob.UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json", CacheControl = "no-cache" },
            Metadata = metadata,
        }, ct);
}
