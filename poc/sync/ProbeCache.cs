using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;

namespace Humlog.Sync;

/// <summary>Video probe results by item id, kept in a private container so each video is probed only once.</summary>
public sealed class ProbeCache(BlobServiceClient service)
{
    // v2 added rotation; a new name makes every video get re-probed once.
    private readonly BlobClient _blob = service.GetBlobContainerClient("sync-state").GetBlobClient("video-probes-v2.json");

    public async Task<Dictionary<string, VideoInfo>> LoadAsync(CancellationToken ct)
    {
        try
        {
            var content = await _blob.DownloadContentAsync(ct);
            return content.Value.Content.ToObjectFromJson<Dictionary<string, VideoInfo>>() ?? [];
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return [];
        }
    }

    public async Task SaveAsync(Dictionary<string, VideoInfo> results, CancellationToken ct)
    {
        await service.GetBlobContainerClient("sync-state").CreateIfNotExistsAsync(cancellationToken: ct);
        await _blob.UploadAsync(BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(results)), overwrite: true, ct);
    }
}
