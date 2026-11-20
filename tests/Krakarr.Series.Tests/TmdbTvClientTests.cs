using System.Net;
using System.Text;
using Krakarr.Core.Configuration;
using Krakarr.Core.Metadata;
using Krakarr.Data;
using Microsoft.Extensions.Configuration;

namespace Krakarr.Series.Tests;

public class TmdbTvClientTests
{
    private sealed class FakeTmdb : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var uri = request.RequestUri!;
            var query = Uri.UnescapeDataString(uri.Query);
            return uri.AbsolutePath switch
            {
                "/3/search/tv" => Json("""
                    {"results":[{"id":1396,"name":"Breaking Bad","original_name":"Breaking Bad","first_air_date":"2008-01-20","overview":"Chemistry teacher.","poster_path":"/p.jpg"}]}
                    """),
                "/3/find/81189" => Json("""{"movie_results":[],"tv_results":[{"id":1396,"name":"Breaking Bad","first_air_date":"2008-01-20"}]}"""),
                var p when p.StartsWith("/3/find/", StringComparison.Ordinal) => Json("""{"movie_results":[],"tv_results":[]}"""),
                "/3/tv/1396" when query.Contains("append_to_response=external_ids") => Json("""
                    {"id":1396,"name":"Breaking Bad","original_name":"Breaking Bad","first_air_date":"2008-01-20","overview":"A chemistry teacher...",
                     "poster_path":"/poster.jpg","backdrop_path":"/fanart.jpg","status":"Ended","episode_run_time":[],
                     "networks":[{"name":"AMC"}],"seasons":[{"season_number":0},{"season_number":1},{"season_number":2}],
                     "external_ids":{"imdb_id":"tt0903747","tvdb_id":81189}}
                    """),
                "/3/tv/1396" when query.Contains("append_to_response=season/0,season/1,season/2") => Json("""
                    {"id":1396,
                     "season/0":{"episodes":[{"id":100,"season_number":0,"episode_number":1,"name":"Special","air_date":null}]},
                     "season/1":{"episodes":[{"id":62085,"season_number":1,"episode_number":1,"name":"Pilot","air_date":"2008-01-20","runtime":58},
                                             {"id":62086,"season_number":1,"episode_number":2,"name":"Cat's in the Bag...","air_date":"2008-01-27","runtime":48}]},
                     "season/2":{"episodes":[{"id":62092,"season_number":2,"episode_number":1,"name":"Seven Thirty-Seven","air_date":"2009-03-08","runtime":47}]}}
                    """),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)),
            };
        }

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Settings(MetadataSettings meta) : ISettingsService
    {
        public Task<T> GetAsync<T>(CancellationToken ct = default) where T : class, ISettingsSection, new() =>
            Task.FromResult(meta as T ?? new T());
        public Task SaveAsync<T>(T value, CancellationToken ct = default) where T : class, ISettingsSection, new() => Task.CompletedTask;
    }

    private static TmdbTvClient Client(FakeTmdb fake, string? key = "v3key") =>
        new(new TmdbApi(new Factory(fake), new ConfigurationBuilder().Build()), new Settings(new MetadataSettings { TmdbApiKey = key, Language = "es-ES" }));

    [Fact]
    public async Task Searches_shows()
    {
        var fake = new FakeTmdb();
        var r = Assert.Single(await Client(fake).SearchAsync("breaking bad", TestContext.Current.CancellationToken));
        Assert.Equal(1396, r.TmdbId);
        Assert.Equal("Breaking Bad", r.Title);
        Assert.Equal(2008, r.Year);
        Assert.Equal("https://image.tmdb.org/t/p/w500/p.jpg", r.PosterUrl);
        var query = Uri.UnescapeDataString(fake.Requests[0].RequestUri!.Query);
        Assert.Contains("query=breaking bad", query);
        Assert.Contains("language=es-ES", query);
        Assert.Contains("api_key=v3key", query);
    }

    [Fact]
    public async Task Reads_show_with_external_ids_and_all_season_episodes()
    {
        var s = await Client(new FakeTmdb()).GetSeriesAsync(1396, TestContext.Current.CancellationToken);
        Assert.Equal(1396, s.TmdbId);
        Assert.Equal(81189, s.TvdbId);
        Assert.Equal("tt0903747", s.ImdbId);
        Assert.Equal("AMC", s.Network);
        Assert.Equal("Ended", s.Status);
        Assert.Equal(new DateOnly(2008, 1, 20), s.FirstAired);
        Assert.Equal(2008, s.Year);
        Assert.Equal("https://image.tmdb.org/t/p/original/fanart.jpg", s.FanartUrl);
        Assert.Equal(48, s.Runtime); // no episode_run_time: median of episode runtimes

        Assert.Equal(4, s.Episodes.Count);
        var pilot = s.Episodes.Single(e => e is { SeasonNumber: 1, EpisodeNumber: 1 });
        Assert.Equal(62085, pilot.TmdbId);
        Assert.Equal("Pilot", pilot.Title);
        Assert.Equal(new DateOnly(2008, 1, 20), pilot.AirDate);
        Assert.Equal(1, pilot.AbsoluteNumber);
        Assert.Equal(3, s.Episodes.Single(e => e.SeasonNumber == 2).AbsoluteNumber);
        Assert.Null(s.Episodes.Single(e => e.SeasonNumber == 0).AbsoluteNumber);
    }

    [Fact]
    public async Task Finds_show_by_tvdb_id()
    {
        var fake = new FakeTmdb();
        var client = Client(fake);
        Assert.Equal(1396, (await client.FindByTvdbAsync(81189, TestContext.Current.CancellationToken))?.TmdbId);
        Assert.Contains("external_source=tvdb_id", fake.Requests[0].RequestUri!.Query);
        Assert.Null(await client.FindByTvdbAsync(5, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sends_v4_tokens_as_bearer()
    {
        var fake = new FakeTmdb();
        var token = new string('t', 200);
        await Client(fake, token).SearchAsync("x", TestContext.Current.CancellationToken);
        Assert.Equal(token, fake.Requests[0].Headers.Authorization?.Parameter);
        Assert.DoesNotContain("api_key", fake.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task Requires_an_api_key()
    {
        var ex = await Assert.ThrowsAsync<MetadataException>(() => Client(new FakeTmdb(), key: null).SearchAsync("x", TestContext.Current.CancellationToken));
        Assert.Contains("TMDB API key", ex.Message);
    }

    [Theory]
    [InlineData("Returning Series", "Continuing")]
    [InlineData("Ended", "Ended")]
    [InlineData("Canceled", "Ended")]
    [InlineData("In Production", "Upcoming")]
    [InlineData("Planned", "Upcoming")]
    [InlineData(null, "Continuing")]
    public void Normalizes_status(string? tmdb, string expected) => Assert.Equal(expected, TmdbTvClient.NormalizeStatus(tmdb));
}
