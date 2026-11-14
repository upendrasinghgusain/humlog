using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Humlog.Sync;

public sealed class SyncFunction(
    AlbumClient album, VideoProbe probe, ProbeCache probes, CatalogueStore store, IConfiguration config, ILogger<SyncFunction> log)
{
    public const string ShareUrlSetting = "ALBUM_SHARE_URL";
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromMinutes(2);

    [Function("Sync")]
    public async Task Run([TimerTrigger("0 */10 * * * *", RunOnStartup = true)] TimerInfo timer, CancellationToken ct)
    {
        var shareUrl = config[ShareUrlSetting];
        if (string.IsNullOrWhiteSpace(shareUrl))
            throw new InvalidOperationException($"App setting {ShareUrlSetting} is not set.");

        // First, so a page update goes out even if reading the album fails.
        var siteFiles = await store.PublishSiteAsync(Path.Combine(AppContext.BaseDirectory, "wwwroot"), ct);
        if (siteFiles > 0)
            log.LogInformation("Published {Count} updated web page files", siteFiles);

        var entries = await album.FetchAllAsync(shareUrl, ct);
        var catalogue = CatalogueBuilder.Build(entries, DateTimeOffset.UtcNow, out var skipped);
        if (skipped > 0)
            log.LogWarning("{Skipped} album entries had an unexpected shape and were skipped", skipped);

        catalogue = CatalogueBuilder.ApplyProbes(catalogue, await ProbeNewVideosAsync(catalogue, ct));

        var current = await store.GetCurrentAsync(ct);
        if (!CatalogueBuilder.IsSafeToPublish(catalogue.Items.Count, current.Count))
        {
            log.LogError("Refusing to publish {New} items over the existing {Old}; keeping the current catalogue",
                catalogue.Items.Count, current.Count);
            return;
        }

        var hash = CatalogueBuilder.ItemsHash(catalogue);
        if (hash == current.Hash)
        {
            log.LogInformation("Album unchanged ({Count} items)", catalogue.Items.Count);
            return;
        }

        await store.PublishAsync(catalogue, hash, ct);
        log.LogInformation("Published catalogue: {Count} items ({Videos} videos), previously {Old}",
            catalogue.Items.Count, catalogue.Items.Count(i => i.Type == "video"), current.Count);
    }

    /// <summary>Probes are slow (Google takes seconds to start serving an original), so each run gets a time budget
    /// and the rest wait for the next run. Unprobed videos stay in the catalogue as they are.</summary>
    private async Task<Dictionary<string, VideoInfo>> ProbeNewVideosAsync(Catalogue catalogue, CancellationToken ct)
    {
        var results = await probes.LoadAsync(ct);
        var pending = catalogue.Items.Where(i => i.Type == "video" && !results.ContainsKey(i.Id)).ToList();
        var probed = 0;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ProbeBudget);
        try
        {
            await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = budget.Token },
                async (video, token) =>
                {
                    if (await probe.ProbeAsync(video.Src, token) is not { } info) return;
                    lock (results) results[video.Id] = info;
                    Interlocked.Increment(ref probed);
                });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogInformation("Probe budget used up; {Left} videos left for the next run", pending.Count - probed);
        }

        if (probed > 0)
        {
            await probes.SaveAsync(results, ct);
            log.LogInformation("Probed {Probed} new videos; {Playable} of {Total} known videos play directly",
                probed, results.Values.Count(v => v.Playable), results.Count);
        }
        return results;
    }
}
