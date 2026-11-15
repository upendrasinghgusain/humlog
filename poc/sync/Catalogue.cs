using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Humlog.Sync;

/// <summary>
/// One photo or video. <see cref="Src"/> is Google's base URL without a size suffix; the web page appends
/// <c>=w{W}-h{H}</c> for photos and posters, and <see cref="Variant"/> (<c>=dv</c> until probed) for the video file.
/// <see cref="AddedUtc"/> is when the item was added to the album, which lets the page play recent additions first.
/// <see cref="Variant"/> is the version of a video the TV should play (see <see cref="VideoProbe"/>).
/// </summary>
public sealed record CatalogueItem(
    string Id, string Type, string Src, int W, int H, DateTimeOffset TakenUtc, DateTimeOffset AddedUtc, long? DurationMs, string? Variant = null);

public sealed record Catalogue(DateTimeOffset GeneratedUtc, IReadOnlyList<CatalogueItem> Items);

public static class CatalogueBuilder
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Most recently added first, then newest taken first; de-duplicated by id. Entries that don't parse are counted in <paramref name="skipped"/>.</summary>
    public static Catalogue Build(IEnumerable<JsonArray> entries, DateTimeOffset now, out int skipped)
    {
        var items = new List<CatalogueItem>();
        var seen = new HashSet<string>();
        skipped = 0;
        foreach (var entry in entries)
        {
            var item = AlbumParser.ToItem(entry);
            if (item is null) { skipped++; continue; }
            if (seen.Add(item.Id)) items.Add(item);
        }
        items.Sort((a, b) => b.AddedUtc != a.AddedUtc ? b.AddedUtc.CompareTo(a.AddedUtc) : b.TakenUtc.CompareTo(a.TakenUtc));
        return new Catalogue(now, items);
    }

    /// <summary>Drops videos the TV can't stream and records which version to play; videos not yet probed are left as they are.</summary>
    public static Catalogue ApplyProbes(Catalogue catalogue, IReadOnlyDictionary<string, VideoInfo> probes)
    {
        var items = new List<CatalogueItem>();
        foreach (var item in catalogue.Items)
        {
            if (item.Type != "video" || !probes.TryGetValue(item.Id, out var info)) items.Add(item);
            else if (info.Variant is not null) items.Add(item with { Variant = info.Variant });
        }
        return catalogue with { Items = items };
    }

    /// <summary>Fail-safe rule: never replace a good catalogue with an empty or much smaller one.</summary>
    public static bool IsSafeToPublish(int newCount, int? previousCount) =>
        newCount > 0 && (previousCount is not { } prev || newCount * 2 >= prev);

    /// <summary>Hash of the items only, so an unchanged album doesn't rewrite the blob every run.</summary>
    public static string ItemsHash(Catalogue catalogue) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(catalogue.Items, Json)))[..16];
}
