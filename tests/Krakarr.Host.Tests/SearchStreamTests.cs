using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Krakarr.Core.Domain;
using Krakarr.Data;
using Krakarr.Indexers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Host.Tests;

/// <summary>Interactive search streamed as newline-delimited JSON while the indexers answer.</summary>
public sealed class SearchStreamTests(KrakarrFactory factory) : IClassFixture<KrakarrFactory>
{
    /// <summary>"Fast" answers at once; "Slow" waits for <see cref="Gate"/>.</summary>
    private sealed class FakeTorznab : ITorznabClient
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<ReleaseInfo>> SearchAsync(Indexer indexer, SearchCriteria criteria, CancellationToken ct = default)
        {
            if (indexer.Name == "Slow") await Gate.Task.WaitAsync(ct);
            return
            [
                new ReleaseInfo
                {
                    Title = $"Stream.Test.2024.1080p.WEB-DL-{indexer.Name}", Guid = $"{indexer.Name}-1", Size = 1000, Seeders = 10,
                    IndexerId = indexer.Id, IndexerName = indexer.Name, IndexerPriority = indexer.Priority,
                },
            ];
        }

        public Task<IndexerCapabilities> GetCapabilitiesAsync(Indexer indexer, bool forceRefresh = false, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Search_streams_each_indexer_as_it_answers()
    {
        var ct = TestContext.Current.CancellationToken;
        var torznab = new FakeTorznab();
        using var app = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddScoped<ITorznabClient>(_ => torznab)));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            db.Indexers.RemoveRange(await db.Indexers.ToListAsync(ct));
            db.Indexers.AddRange(
                new Indexer { Name = "Fast", BaseUrl = "http://fast", Enabled = true, Priority = 25 },
                new Indexer { Name = "Slow", BaseUrl = "http://slow", Enabled = true, Priority = 25 });
            await db.SaveChangesAsync(ct);
        }

        var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", ct);
        var auth = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        (await client.PostAsJsonAsync(auth, new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, ct)).EnsureSuccessStatusCode();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/search?query=stream+test");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(SearchStream.ContentType));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SearchStream.ContentType, response.Content.Headers.ContentType?.MediaType);

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        async Task<JsonElement> NextAsync() => JsonDocument.Parse((await reader.ReadLineAsync(ct))!).RootElement;

        var first = await NextAsync();
        Assert.Equal("indexers", first.GetProperty("type").GetString());
        Assert.All(first.GetProperty("indexers").EnumerateArray(), i => Assert.Equal("Pending", i.GetProperty("state").GetString()));

        // "Slow" hasn't answered yet, but "Fast"'s results are already here.
        var fast = await NextAsync();
        Assert.Equal("releases", fast.GetProperty("type").GetString());
        Assert.Equal("Fast", fast.GetProperty("indexer").GetProperty("indexerName").GetString());
        Assert.Equal("Fast-1", Assert.Single(fast.GetProperty("releases").EnumerateArray()).GetProperty("release").GetProperty("guid").GetString());

        torznab.Gate.SetResult();
        var slow = await NextAsync();
        Assert.Equal("Slow", slow.GetProperty("indexer").GetProperty("indexerName").GetString());
        var done = await NextAsync();
        Assert.Equal("done", done.GetProperty("type").GetString());
        Assert.Equal(2, done.GetProperty("order").GetArrayLength());
        Assert.All(done.GetProperty("indexers").EnumerateArray(), i => Assert.Equal("Done", i.GetProperty("state").GetString()));
        Assert.Null(await reader.ReadLineAsync(ct));
    }
}
