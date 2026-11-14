using System.Buffers.Binary;
using System.Text;

namespace Humlog.Sync;

/// <summary>What one probe of a video's original file found.</summary>
/// <param name="Playable">Can a TV browser stream the original directly?</param>
/// <param name="Rotation">Clockwise degrees the phone asked players to rotate the picture by (0, 90, 180, 270).</param>
public sealed record VideoInfo(bool Playable, int Rotation);

/// <summary>
/// Inspects the start of a video's original file. Google serves originals without range support, so a file only
/// plays if its index ('moov') comes before the media data and the codec is H.264.
/// </summary>
public sealed class VideoProbe(HttpClient http)
{
    public const int SampleBytes = 256 * 1024;
    public const long MaxBytes = 200L * 1024 * 1024;

    /// <returns>The findings, or null if the probe itself failed (try again next run).</returns>
    public async Task<VideoInfo?> ProbeAsync(string src, CancellationToken ct)
    {
        try
        {
            using var res = await http.GetAsync(src + "=dv", HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) return null;
            if (res.Content.Headers.ContentLength > MaxBytes) return new VideoInfo(false, 0);

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[SampleBytes];
            var read = await stream.ReadAtLeastAsync(buffer, SampleBytes, throwOnEndOfStream: false, ct);
            var head = buffer.AsSpan(0, read);
            return new VideoInfo(Mp4.IsStreamableH264(head), Mp4.Rotation(head));
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

    /// <summary>Rotation from the video track's header ('tkhd') matrix; 0 if absent or unreadable.</summary>
    public static int Rotation(ReadOnlySpan<byte> head)
    {
        var moov = LeadingMoov(head);
        for (var start = 0; ;)
        {
            var found = moov[start..].IndexOf("tkhd"u8);
            if (found < 0) return 0;
            var typeAt = start + found;
            start = typeAt + 4;
            if (typeAt < 4) continue;

            // tkhd: size, type, version(1) + flags(3), times/ids (20 or 32 bytes by version), 16 more, then the matrix.
            var box = moov[(typeAt - 4)..];
            var matrixAt = box[8] == 1 ? 60 : 48;
            if (box.Length < matrixAt + 44) return 0;
            if (BinaryPrimitives.ReadUInt32BigEndian(box[(matrixAt + 36)..]) == 0) continue; // width 0: audio track

            var a = BinaryPrimitives.ReadInt32BigEndian(box[matrixAt..]);
            var b = BinaryPrimitives.ReadInt32BigEndian(box[(matrixAt + 4)..]);
            var degrees = (int)Math.Round(Math.Atan2(b, a) * 180 / Math.PI);
            return (degrees + 360) % 360;
        }
    }

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
