using System.Buffers.Binary;
using System.Net;
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

    [Fact]
    public void Probes_drop_unplayable_videos_and_record_the_variant()
    {
        var entries = new[]
        {
            """["P",["https://lh3/p",10,10],3,"",0,0,[],[],2,{}]""",
            """["V1",["https://lh3/v1",10,10],2,"",0,0,[],[],2,{"76647426":[1000]}]""",
            """["V2",["https://lh3/v2",10,10],1,"",0,0,[],[],2,{"76647426":[1000]}]""",
            """["V3",["https://lh3/v3",10,10],0,"",0,0,[],[],2,{"76647426":[1000]}]""",
        }.Select(e => JsonNode.Parse(e)!.AsArray());
        var catalogue = CatalogueBuilder.Build(entries, DateTimeOffset.UnixEpoch, out _);
        var probes = new Dictionary<string, VideoInfo> { ["V1"] = new(null), ["V2"] = new("=m22"), ["P"] = new(null) };

        var result = CatalogueBuilder.ApplyProbes(catalogue, probes);

        Assert.Equal(["P", "V2", "V3"], result.Items.Select(i => i.Id));
        Assert.Equal([null, "=m22", null], result.Items.Select(i => i.Variant));
    }

    private static readonly byte[] Streamable = File(Box("ftyp", 16), Box("moov", 64, "avc1"), Box("mdat", 32));
    private static readonly byte[] NotStreamable = File(Box("ftyp", 16), Box("mdat", 32), Box("moov", 64, "avc1"));

    [Theory]
    [InlineData("200 200 200", "=m37")]   // best transcode first
    [InlineData("404 200 200", "=m22")]   // no 1080p transcode
    [InlineData("500 200 200", "=m22")]   // a failing variant doesn't block a working one
    [InlineData("404 404 200", "=dv")]    // no transcodes (very old or tiny videos): the original
    [InlineData("404 403 bad", null)]     // nothing plays
    [InlineData("404 500 bad", "retry")]  // nothing played, but one variant couldn't be checked
    public async Task Probe_picks_the_first_variant_that_streams(string responses, string? expected)
    {
        var codes = responses.Split(' ');
        var handler = new FakeVariants(url => codes[Array.IndexOf(VideoProbe.Variants, url[url.LastIndexOf('=')..])]);

        var info = await new VideoProbe(new HttpClient(handler)).ProbeAsync("https://lh3/v", CancellationToken.None);

        Assert.Equal(expected, info is null ? "retry" : info.Variant);
    }

    /// <summary>Answers each variant URL with a status code, or "bad" for a 200 with an unstreamable file.</summary>
    private sealed class FakeVariants(Func<string, string> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var code = answer(request.RequestUri!.ToString());
            var res = new HttpResponseMessage(code == "bad" ? HttpStatusCode.OK : (HttpStatusCode)int.Parse(code));
            if (res.IsSuccessStatusCode) res.Content = new ByteArrayContent(code == "bad" ? NotStreamable : Streamable);
            return Task.FromResult(res);
        }
    }
}
