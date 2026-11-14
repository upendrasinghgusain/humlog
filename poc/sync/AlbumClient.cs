using System.Text.Json.Nodes;
using System.Web;
using Microsoft.Extensions.Logging;

namespace Humlog.Sync;

/// <summary>Downloads every entry of a public shared album: the share page, then each further page.</summary>
public sealed class AlbumClient(HttpClient http, ILogger<AlbumClient> log)
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";

    private const string BatchUrl = "https://photos.google.com/_/PhotosUi/data/batchexecute";
    private const int MaxPages = 200;

    public async Task<List<JsonArray>> FetchAllAsync(string shareUrl, CancellationToken ct)
    {
        using var res = await http.GetAsync(shareUrl, ct);
        res.EnsureSuccessStatusCode();

        // The short link redirects to photos.google.com/share/{albumId}?key={shareKey}.
        var finalUri = res.RequestMessage?.RequestUri ?? throw new AlbumFormatException("No final URL after redirect.");
        var albumId = finalUri.Segments[^1];
        var key = HttpUtility.ParseQueryString(finalUri.Query)["key"]
            ?? throw new AlbumFormatException("Share key missing from the album URL.");

        var page = AlbumParser.ParseSharePage(await res.Content.ReadAsStringAsync(ct));
        var all = new List<JsonArray>(page.Entries);
        var pages = 1;

        for (var token = page.NextToken; token is not null; token = page.NextToken)
        {
            if (++pages > MaxPages)
                throw new AlbumFormatException($"More than {MaxPages} pages; stopping.");

            var request = new JsonArray(albumId, token, null, key).ToJsonString();
            var freq = new JsonArray(new JsonArray(new JsonArray("snAcKc", request, null, "generic"))).ToJsonString();
            using var body = new FormUrlEncodedContent([new("f.req", freq)]);
            using var pageRes = await http.PostAsync(BatchUrl, body, ct);
            pageRes.EnsureSuccessStatusCode();

            page = AlbumParser.ParseBatchResponse(await pageRes.Content.ReadAsStringAsync(ct));
            all.AddRange(page.Entries);
        }

        log.LogInformation("Fetched {Count} album entries over {Pages} pages", all.Count, pages);
        return all;
    }
}
