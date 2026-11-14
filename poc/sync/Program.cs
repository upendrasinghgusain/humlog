using Azure.Storage.Blobs;
using Humlog.Sync;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services.AddHttpClient<AlbumClient>(http =>
{
    http.DefaultRequestHeaders.UserAgent.ParseAdd(AlbumClient.UserAgent);
    http.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddHttpClient<VideoProbe>(http => http.Timeout = TimeSpan.FromSeconds(30));
// The website lives in its own firewalled account (reached over the VNet); the function's own account keeps sync state.
builder.Services.AddSingleton(_ => new CatalogueStore(new BlobServiceClient(builder.Configuration["WEB_STORAGE"])));
builder.Services.AddSingleton(_ => new ProbeCache(new BlobServiceClient(builder.Configuration["AzureWebJobsStorage"])));

builder.Build().Run();
