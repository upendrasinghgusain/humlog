using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Humlog.Sync;

/// <summary>What one probe of a video found.</summary>
/// <param name="Variant">The URL suffix the TV should play (<c>=m37</c>, <c>=m22</c> or <c>=dv</c>), or null if none plays.</param>
public sealed record VideoInfo(string? Variant);

/// <summary>
/// Picks the version of a video the TV should play. Google's own transcodes come first: they are upright (phones
/// record portrait video sideways with a rotation flag, which the TV's video layer ignores), H.264, seekable and
/// much smaller than the original. They only load without a Referer header, so the page sends none.
/// The original is the fallback; Google serves it without range support, so it only plays if its index ('moov')
/// comes before the media data and the codec is H.264.
/// </summary>
public sealed class VideoProbe(HttpClient http)
{
    public const int SampleBytes = 256 * 1024;
    public const long MaxBytes = 200L * 1024 * 1024;

    /// <summary>1080p transcode, 720p transcode, original. Not every video has every transcode.</summary>
    public static readonly string[] Variants = ["=m37", "=m22", "=dv"];

    /// <returns>The findings, or null if no variant played and at least one couldn't be checked (try again next run).</returns>
    public async Task<VideoInfo?> ProbeAsync(string src, CancellationToken ct)
    {
        var uncertain = false;
        foreach (var variant in Variants)
        {
            switch (await PlaysAsync(src + variant, ct))
            {
                case true: return new VideoInfo(variant);
                case null: uncertain = true; break;
            }
        }
        return uncertain ? null : new VideoInfo(null);
    }

    /// <returns>Whether the TV can stream this URL; null if that couldn't be told (server or network error).</returns>
    private async Task<bool?> PlaysAsync(string url, CancellationToken ct)
    {
        try
        {
            using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden) return false;
            if (!res.IsSuccessStatusCode) return null;
            if (res.Content.Headers.ContentLength > MaxBytes) return false;

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[SampleBytes];
            var read = await stream.ReadAtLeastAsync(buffer, SampleBytes, throwOnEndOfStream: false, ct);
            return Mp4.IsStreamableH264(buffer.AsSpan(0, read));
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return null; // network error or HttpClient timeout
        }
    }
}

public static class Mp4
{
    /// <summary>'moov' must precede 'mdat' and describe avc1.</summary>
    public static bool IsStreamableH264(ReadOnlySpan<byte> head) => LeadingMoov(head).IndexOf("avc1"u8) >= 0;

    /// <summary>The top-level 'moov' box if it comes before 'mdat' within these bytes, else empty.</summary>
    private static ReadOnlySpan<byte> LeadingMoov(ReadOnlySpan<byte> head)
    {
        var pos = 0L;
        while (pos + 8 <= head.Length)
        {
            var box = head[(int)pos..];
            long size = BinaryPrimitives.ReadUInt32BigEndian(box);
            var type = Encoding.ASCII.GetString(box.Slice(4, 4));
            if (size == 1 && box.Length >= 16) size = (long)BinaryPrimitives.ReadUInt64BigEndian(box[8..]);

            if (type == "mdat") return [];
            if (type == "moov") return box[..(int)Math.Min(box.Length, size)];
            if (size < 8) return [];
            pos += size;
        }
        return [];
    }
}
