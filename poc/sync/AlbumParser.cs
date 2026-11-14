using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Humlog.Sync;

/// <summary>One page of raw album entries plus the continuation token for the next page (null on the last page).</summary>
public sealed record AlbumPage(IReadOnlyList<JsonArray> Entries, string? NextToken);

public sealed class AlbumFormatException(string message) : Exception(message);

/// <summary>
/// Parses Google Photos' public share page and its paging responses. This is an undocumented format
/// (accepted for the PoC, see spec-azure-poc.md §2) so every step fails loudly rather than guessing.
/// </summary>
public static partial class AlbumParser
{
    /// <summary>Extra-data key present only on videos; its value starts with the duration in ms.</summary>
    internal const string VideoKey = "76647426";

    [GeneratedRegex(@"key: 'ds:1', hash: '\d+', data:(.*?), sideChannel: \{\}\}\);</script>", RegexOptions.Singleline)]
    private static partial Regex InitialData();

    public static AlbumPage ParseSharePage(string html)
    {
        var match = InitialData().Match(html);
        if (!match.Success)
            throw new AlbumFormatException("Album data block 'ds:1' not found in the share page.");
        return ParsePayload(match.Groups[1].Value);
    }

    public static AlbumPage ParseBatchResponse(string body)
    {
        var line = body.Split('\n').FirstOrDefault(l => l.StartsWith("[[\"wrb.fr\"", StringComparison.Ordinal))
            ?? throw new AlbumFormatException("Paging response has no 'wrb.fr' line.");
        var payload = JsonNode.Parse(line)?[0]?[2]?.GetValue<string>()
            ?? throw new AlbumFormatException("Paging response has no payload.");
        return ParsePayload(payload);
    }

    private static AlbumPage ParsePayload(string json)
    {
        if (JsonNode.Parse(json) is not JsonArray root || root.Count < 2)
            throw new AlbumFormatException("Album payload is not the expected array.");

        var entries = root[1] is JsonArray list ? list.OfType<JsonArray>().ToList() : [];
        var token = root.Count > 2 && root[2] is JsonValue v && v.TryGetValue<string>(out var t) && t.Length > 0 ? t : null;
        return new AlbumPage(entries, token);
    }

    /// <summary>Maps one raw entry to a catalogue item, or null if it doesn't have the expected shape.</summary>
    public static CatalogueItem? ToItem(JsonArray entry)
    {
        if (entry.Count < 3
            || !TryString(entry[0], out var id)
            || entry[1] is not JsonArray media || media.Count < 3
            || !TryString(media[0], out var url)
            || !TryLong(media[1], out var w) || !TryLong(media[2], out var h)
            || !TryLong(entry[2], out var takenMs))
            return null;

        var extra = entry[^1] as JsonObject;
        var isVideo = extra?[VideoKey] is not null;
        long? durationMs = isVideo && extra![VideoKey] is JsonArray info && info.Count > 0 && TryLong(info[0], out var d) ? d : null;

        return new CatalogueItem(
            Id: id,
            Type: isVideo ? "video" : "photo",
            Src: url,
            W: (int)w,
            H: (int)h,
            TakenUtc: DateTimeOffset.FromUnixTimeMilliseconds(takenMs),
            DurationMs: durationMs);
    }

    private static bool TryString(JsonNode? node, out string value)
    {
        value = node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        return value.Length > 0;
    }

    private static bool TryLong(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue(out long l)) { value = l; return true; }
        if (v.TryGetValue(out double dbl)) { value = (long)dbl; return true; }
        return false;
    }
}
