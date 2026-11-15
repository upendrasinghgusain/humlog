using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Humlog.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace Humlog.Sync.Tests;

/// <summary>Synthetic fixtures shaped like the real share page (observed 2026-11-14); no real album data.</summary>
public class AlbumTests
{
    private const string Photo =
        """["AF1QipPhoto1",["https://lh3.googleusercontent.com/pw/photo1",2048,1536,null,null,null,null,null,[null,null,1]],1376813242000,"x",-25200000,1794586995510,["AF1QipAlbum"],[[2],[31,0,1]],2,{"15":15951,"101428965":[0,"vd911023"]}]""";
    private const string Video =
        """["AF1QipVideo1",["https://lh3.googleusercontent.com/pw/video1",1080,1920],1794507214000,"y",0,1794586995510,[],[],2,{"76647426":[287046,null,1080,1920]}]""";
    private const string Malformed = """["AF1QipBad",null,1]""";
    private const string Later =
        """["AF1QipPhoto2",["https://lh3.googleusercontent.com/pw/photo2",3060,4080],1794600000000,"z",0,1,[],[],2,{}]""";

    private static string SharePage(string data) =>
        $"<html><script nonce=\"n\">AF_initDataCallback({{key: 'ds:1', hash: '3', data:{data}, sideChannel: {{}}}});</script></html>";

    private static string BatchResponse(string payload) =>
        ")]}'\n\n123\n" + JsonSerializer.Serialize(new object?[] { new object?[] { "wrb.fr", "snAcKc", payload, null, null, null, "generic" } }) + "\n58\n[[\"di\",1]]\n";

    [Fact]
    public void Share_page_yields_entries_and_token()
    {
        var page = AlbumParser.ParseSharePage(SharePage($"[null,[{Photo},{Video}],\"tok1\",[\"meta\"]]"));

        Assert.Equal(2, page.Entries.Count);
        Assert.Equal("tok1", page.NextToken);
    }

    [Fact]
    public void Last_page_has_no_token()
    {
        var page = AlbumParser.ParseBatchResponse(BatchResponse($"[null,[{Later}],null]"));

        Assert.Single(page.Entries);
        Assert.Null(page.NextToken);
    }

    [Fact]
    public void Changed_page_format_fails_loudly()
    {
        Assert.Throws<AlbumFormatException>(() => AlbumParser.ParseSharePage("<html>no data here</html>"));
        Assert.Throws<AlbumFormatException>(() => AlbumParser.ParseBatchResponse(")]}'\n[[\"er\",null]]"));
    }

    [Fact]
    public void Photo_and_video_are_mapped()
    {
        var photo = AlbumParser.ToItem(JsonNode.Parse(Photo)!.AsArray())!;
        var video = AlbumParser.ToItem(JsonNode.Parse(Video)!.AsArray())!;

        Assert.Equal(new CatalogueItem("AF1QipPhoto1", "photo", "https://lh3.googleusercontent.com/pw/photo1", 2048, 1536,
            DateTimeOffset.FromUnixTimeMilliseconds(1376813242000), DateTimeOffset.FromUnixTimeMilliseconds(1794586995510), null), photo);
        Assert.Equal("video", video.Type);
        Assert.Equal(287046, video.DurationMs);
        Assert.Equal((1080, 1920), (video.W, video.H));
    }

    [Fact]
    public void Added_time_falls_back_to_taken_time_when_missing()
    {
        var item = AlbumParser.ToItem(JsonNode.Parse("""["AF1QipShort",["https://x",10,10],1794600000000]""")!.AsArray())!;

        Assert.Equal(item.TakenUtc, item.AddedUtc);
    }

    [Fact]
    public void Catalogue_is_recently_added_first_then_newest_taken_deduplicated_and_skips_malformed()
    {
        // Later was taken last but added long ago; Photo and Video were added together, Video taken more recently.
        var entries = new[] { Later, Photo, Video, Malformed, Photo }.Select(e => JsonNode.Parse(e)!.AsArray());

        var catalogue = CatalogueBuilder.Build(entries, DateTimeOffset.UnixEpoch, out var skipped);

        Assert.Equal(["AF1QipVideo1", "AF1QipPhoto1", "AF1QipPhoto2"], catalogue.Items.Select(i => i.Id));
        Assert.Equal(1, skipped);
    }

    [Theory]
    [InlineData(0, null, false)]
    [InlineData(10, null, true)]
    [InlineData(1130, 1130, true)]
    [InlineData(565, 1130, true)]
    [InlineData(564, 1130, false)]
    [InlineData(0, 1130, false)]
    public void Never_replaces_a_good_catalogue_with_a_much_smaller_one(int newCount, int? previous, bool expected) =>
        Assert.Equal(expected, CatalogueBuilder.IsSafeToPublish(newCount, previous));

    [Fact]
    public void Hash_ignores_generated_time_but_tracks_items()
    {
        var a = CatalogueBuilder.Build([JsonNode.Parse(Photo)!.AsArray()], DateTimeOffset.UnixEpoch, out _);
        var b = a with { GeneratedUtc = DateTimeOffset.UtcNow };
        var c = CatalogueBuilder.Build([JsonNode.Parse(Later)!.AsArray()], DateTimeOffset.UnixEpoch, out _);

        Assert.Equal(CatalogueBuilder.ItemsHash(a), CatalogueBuilder.ItemsHash(b));
        Assert.NotEqual(CatalogueBuilder.ItemsHash(a), CatalogueBuilder.ItemsHash(c));
    }

    [Fact]
    public void Serialised_catalogue_uses_the_web_page_field_names()
    {
        var catalogue = CatalogueBuilder.Build([JsonNode.Parse(Video)!.AsArray()], DateTimeOffset.UnixEpoch, out _);

        var item = JsonNode.Parse(JsonSerializer.Serialize(catalogue, CatalogueBuilder.Json))!["items"]![0]!.AsObject();

        Assert.Equal(["id", "type", "src", "w", "h", "takenUtc", "addedUtc", "durationMs"], item.Select(p => p.Key));
    }

    [Fact]
    public async Task Client_follows_the_short_link_and_pages_with_token_and_key()
    {
        var handler = new FakeGoogle(
            SharePage($"[null,[{Photo}],\"tok1\"]"),
            BatchResponse($"[null,[{Video}],\"tok2\"]"),
            BatchResponse($"[null,[{Later}],null]"));
        var client = new AlbumClient(new HttpClient(handler), NullLogger<AlbumClient>.Instance);

        var entries = await client.FetchAllAsync("https://photos.app.goo.gl/short", CancellationToken.None);

        Assert.Equal(3, entries.Count);
        Assert.Equal(["""["ALBUMID","tok1",null,"KEY"]""", """["ALBUMID","tok2",null,"KEY"]"""], handler.PageRequests);
    }

    private sealed class FakeGoogle(string sharePage, params string[] batches) : HttpMessageHandler
    {
        private int _next;
        public List<string> PageRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
            {
                // Simulate the redirect: the response reports the final album URL.
                var final = new HttpRequestMessage(HttpMethod.Get, "https://photos.google.com/share/ALBUMID?key=KEY");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sharePage), RequestMessage = final };
            }

            var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct));
            PageRequests.Add(JsonNode.Parse(form["f.req"]!)![0]![0]![1]!.GetValue<string>());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(batches[_next++]) };
        }
    }
}
