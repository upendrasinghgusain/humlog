using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Humlog.Sync;

namespace Humlog.Sync.Tests;

public class VideoTests
{
    private static byte[] Box(string type, byte[] content)
    {
        var box = new byte[8 + content.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        content.CopyTo(box, 8);
        return box;
    }

    private static byte[] Box(string type, int contentLength, string? marker = null)
    {
        var content = new byte[contentLength];
        if (marker is not null) Encoding.ASCII.GetBytes(marker).CopyTo(content, 8);
        return Box(type, content);
    }

    /// <summary>A version-0 track header with the given matrix (a, b, c, d as whole numbers) and width.</summary>
    private static byte[] Tkhd(int a, int b, int c, int d, int width)
    {
        var content = new byte[84];
        foreach (var (value, offset) in new[] { (a, 40), (b, 44), (c, 52), (d, 56) })
            BinaryPrimitives.WriteInt32BigEndian(content.AsSpan(offset), value << 16);
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(76), (uint)width << 16);
        return Box("tkhd", content);
    }

    private static byte[] File(params byte[][] boxes) => boxes.SelectMany(b => b).ToArray();

    [Fact]
    public void H264_with_index_first_is_streamable() =>
        Assert.True(Mp4.IsStreamableH264(File(Box("ftyp", 16), Box("moov", 64, "avc1"), Box("mdat", 32))));

    [Fact]
    public void Index_at_the_end_is_not_streamable() =>
        Assert.False(Mp4.IsStreamableH264(File(Box("ftyp", 16), Box("wide", 0), Box("mdat", 32), Box("moov", 64, "avc1"))));

    [Fact]
    public void HEVC_is_not_streamable() =>
        Assert.False(Mp4.IsStreamableH264(File(Box("ftyp", 16), Box("moov", 64, "hvc1"), Box("mdat", 32))));

    [Fact]
    public void Garbage_is_not_streamable()
    {
        Assert.False(Mp4.IsStreamableH264([]));
        Assert.False(Mp4.IsStreamableH264(new byte[64]));
    }

    [Theory]
    [InlineData(1, 0, 0, 1, 0)]
    [InlineData(0, 1, -1, 0, 90)]
    [InlineData(-1, 0, 0, -1, 180)]
    [InlineData(0, -1, 1, 0, 270)]
    public void Rotation_comes_from_the_video_track_matrix(int a, int b, int c, int d, int expected)
    {
        var audio = Tkhd(1, 0, 0, 1, width: 0);
        var video = Tkhd(a, b, c, d, width: 1920);

        Assert.Equal(expected, Mp4.Rotation(File(Box("ftyp", 16), Box("moov", File(audio, video)), Box("mdat", 32))));
    }

    [Fact]
    public void No_track_header_means_no_rotation() =>
        Assert.Equal(0, Mp4.Rotation(File(Box("ftyp", 16), Box("moov", 64, "avc1"))));

    [Fact]
    public void Probes_drop_unplayable_videos_and_record_rotation()
    {
        var entries = new[]
        {
            """["P",["https://lh3/p",10,10],3,"",0,0,[],[],2,{}]""",
            """["V1",["https://lh3/v1",10,10],2,"",0,0,[],[],2,{"76647426":[1000]}]""",
            """["V2",["https://lh3/v2",10,10],1,"",0,0,[],[],2,{"76647426":[1000]}]""",
            """["V3",["https://lh3/v3",10,10],0,"",0,0,[],[],2,{"76647426":[1000]}]""",
        }.Select(e => JsonNode.Parse(e)!.AsArray());
        var catalogue = CatalogueBuilder.Build(entries, DateTimeOffset.UnixEpoch, out _);
        var probes = new Dictionary<string, VideoInfo> { ["V1"] = new(false, 0), ["V2"] = new(true, 90), ["P"] = new(false, 0) };

        var result = CatalogueBuilder.ApplyProbes(catalogue, probes);

        Assert.Equal(["P", "V2", "V3"], result.Items.Select(i => i.Id));
        Assert.Equal([null, 90, null], result.Items.Select(i => i.Rotation));
    }
}
