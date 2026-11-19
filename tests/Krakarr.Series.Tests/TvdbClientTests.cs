using System.Net;
using System.Text;
using Krakarr.Core.Configuration;
using Krakarr.Data;

namespace Krakarr.Series.Tests;

public class TvdbClientTests
{
    private sealed class FakeTvdb : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public int Logins { get; private set; }
        public bool ExpireOnce { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Paths.Add(path);
            if (path.EndsWith("/login", StringComparison.Ordinal))
            {
                Logins++;
                return Json("""{"data":{"token":"tok"}}""");
            }

            if (request.Headers.Authorization?.Parameter != "tok") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            if (ExpireOnce)
            {
                ExpireOnce = false;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            return path switch
            {
                var p when p.StartsWith("/v4/search", StringComparison.Ordinal) => Json("""
                    {"data":[{"tvdb_id":"81189","name":"Breaking Bad","year":"2008","image_url":"http://img/p.jpg","network":"AMC","status":"Ended","translations":{"eng":"Breaking Bad"},"overviews":{"eng":"Chemistry teacher."}}]}
                    """),
                "/v4/series/81189/extended" => Json("""
                    {"data":{"id":81189,"name":"Breaking Bad (orig)","year":"2008","firstAired":"2008-01-20","averageRuntime":47,
                     "status":{"name":"Ended"},"originalNetwork":{"name":"AMC"},
                     "remoteIds":[{"id":"tt0903747","sourceName":"IMDB"}],
                     "artworks":[{"image":"http://img/poster.jpg","type":2,"score":10},{"image":"http://img/fanart.jpg","type":3,"score":5}]}}
                    """),
                "/v4/series/81189/translations/eng" => Json("""{"data":{"name":"Breaking Bad","overview":"A chemistry teacher..."}}"""),
                "/v4/series/81189/episodes/default/eng?page=0" => Json("""
                    {"data":{"episodes":[{"id":1,"seasonNumber":1,"number":1,"name":"Pilot","aired":"2008-01-20","runtime":58},
                                         {"id":2,"seasonNumber":1,"number":2,"name":"Cat's in the Bag...","aired":"2008-01-27"}]},
                     "links":{"next":"https://api4.thetvdb.com/v4/series/81189/episodes/default/eng?page=1"}}
                    """),
                "/v4/series/81189/episodes/default/eng?page=1" => Json("""
                    {"data":{"episodes":[{"id":3,"seasonNumber":0,"number":1,"name":"Special","aired":null}]},"links":{"next":null}}
                    """),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)),
            };
        }

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api4.thetvdb.com/v4/") };
    }

    private sealed class Settings(MetadataSettings meta) : ISettingsService
    {
        public Task<T> GetAsync<T>(CancellationToken ct = default) where T : class, ISettingsSection, new() =>
            Task.FromResult(meta as T ?? new T());
        public Task SaveAsync<T>(T value, CancellationToken ct = default) where T : class, ISettingsSection, new() => Task.CompletedTask;
    }

    // A unique key per test avoids sharing the client's static token cache between tests.
    private static TvdbClient Client(FakeTvdb fake) => new(new Factory(fake), new Settings(new MetadataSettings { TvdbApiKey = Guid.NewGuid().ToString() }));

    [Fact]
    public async Task Searches_series()
    {
        var results = await Client(new FakeTvdb()).SearchAsync("breaking", TestContext.Current.CancellationToken);
        var r = Assert.Single(results);
        Assert.Equal(81189, r.TvdbId);
        Assert.Equal("Breaking Bad", r.Title);
        Assert.Equal(2008, r.Year);
        Assert.Equal("Chemistry teacher.", r.Overview);
    }

    [Fact]
    public async Task Reads_series_with_translation_artwork_and_all_episode_pages()
    {
        var fake = new FakeTvdb();
        var info = await Client(fake).GetSeriesAsync(81189, TestContext.Current.CancellationToken);

        Assert.Equal("Breaking Bad", info.Title); // translated name wins
        Assert.Equal("A chemistry teacher...", info.Overview);
        Assert.Equal("tt0903747", info.ImdbId);
        Assert.Equal("http://img/poster.jpg", info.PosterUrl);
        Assert.Equal("http://img/fanart.jpg", info.FanartUrl);
        Assert.Equal("Ended", info.Status);
        Assert.Equal("AMC", info.Network);
        Assert.Equal(47, info.Runtime);
        Assert.Equal(new DateOnly(2008, 1, 20), info.FirstAired);
        Assert.Equal(3, info.Episodes.Count);
        Assert.Equal("Pilot", info.Episodes[0].Title);
        Assert.Null(info.Episodes[2].AirDate);
    }

    [Fact]
    public async Task Logs_in_again_when_the_token_expires()
    {
        var fake = new FakeTvdb();
        var client = Client(fake);
        await client.SearchAsync("a", TestContext.Current.CancellationToken);
        fake.ExpireOnce = true;
        await client.SearchAsync("b", TestContext.Current.CancellationToken);
        Assert.Equal(2, fake.Logins);
    }

    [Fact]
    public async Task Missing_api_key_is_reported()
    {
        var client = new TvdbClient(new Factory(new FakeTvdb()), new Settings(new MetadataSettings()));
        var ex = await Assert.ThrowsAsync<TvdbException>(() => client.SearchAsync("x", TestContext.Current.CancellationToken));
        Assert.Contains("API key", ex.Message);
    }
}
