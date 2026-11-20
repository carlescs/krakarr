using System.Net;
using System.Text;
using Krakarr.Core.Configuration;
using Krakarr.Core.Metadata;
using Krakarr.Data;
using Microsoft.Extensions.Configuration;

namespace Krakarr.Series.Tests;

public class TmdbSeriesDiscoveryTests
{
    private sealed class FakeTmdb : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            return uri.AbsolutePath switch
            {
                "/3/tv/popular" => Json("""
                    {"page":1,"total_pages":3,"results":[{"id":1396,"name":"Breaking Bad","first_air_date":"2008-01-20","poster_path":"/p.jpg","vote_average":8.9}]}
                    """),
                "/3/find/81189" => Json("""{"tv_results":[{"id":1396,"name":"Breaking Bad"}]}"""),
                "/3/tv/1396" => Json("""
                    {"id":1396,"name":"Breaking Bad","first_air_date":"2008-01-20","last_air_date":"2013-09-29","status":"Ended","number_of_episodes":62,
                     "episode_run_time":[47],"genres":[{"name":"Drama"}],"networks":[{"name":"AMC"}],
                     "created_by":[{"id":66633,"name":"Vince Gilligan","profile_path":"/v.jpg"}],
                     "seasons":[{"season_number":0,"name":"Specials","episode_count":9},{"season_number":1,"name":"Season 1","episode_count":7,"air_date":"2008-01-20"}],
                     "external_ids":{"tvdb_id":81189,"imdb_id":"tt0903747"},
                     "aggregate_credits":{"cast":[
                       {"id":84497,"name":"Aaron Paul","order":1,"total_episode_count":62,"roles":[{"character":"Jesse Pinkman"}]},
                       {"id":17419,"name":"Bryan Cranston","order":0,"total_episode_count":62,"roles":[{"character":"Walter White"},{"character":"Heisenberg"}]}],
                      "crew":[{"id":66633,"name":"Vince Gilligan","jobs":[{"job":"Director"},{"job":"Gaffer"}]},{"id":5,"name":"Grip Person","jobs":[{"job":"Grip"}]}]}}
                    """),
                "/3/tv/1396/similar" => Json("""{"page":1,"total_pages":1,"results":[{"id":60059,"name":"Better Call Saul","first_air_date":"2015-02-08"}]}"""),
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

    private sealed class Settings : ISettingsService
    {
        public Task<T> GetAsync<T>(CancellationToken ct = default) where T : class, ISettingsSection, new() =>
            Task.FromResult(new MetadataSettings { TmdbApiKey = "v3key", SeriesSource = SeriesMetadataSource.Tvdb } as T ?? new T());
        public Task SaveAsync<T>(T value, CancellationToken ct = default) where T : class, ISettingsSection, new() => Task.CompletedTask;
    }

    private static TmdbSeriesDiscovery Discovery(FakeTmdb fake) =>
        new(new TmdbApi(new Factory(fake), new ConfigurationBuilder().Build()), new Settings(), TimeProvider.System);

    [Fact]
    public async Task Reads_popular_shows()
    {
        var page = await Discovery(new FakeTmdb()).ListAsync(SeriesList.Popular, 1, TestContext.Current.CancellationToken);
        var show = Assert.Single(page.Results);
        Assert.Equal(1396, show.TmdbId);
        Assert.Equal(2008, show.Year);
        Assert.Equal(8.9, show.Rating);
        Assert.Equal(3, page.TotalPages);
    }

    [Fact]
    public async Task Resolves_the_tmdb_id_of_a_show_only_known_by_tvdb_id()
    {
        var fake = new FakeTmdb();
        var similar = await Discovery(fake).SimilarAsync(new SeriesIds(null, 81189), TestContext.Current.CancellationToken);
        Assert.Equal("Better Call Saul", Assert.Single(similar).Title);
        Assert.Equal("/3/find/81189", fake.Requests[0].AbsolutePath);
    }

    [Fact]
    public async Task Batch_recommendations_skip_unknown_shows()
    {
        var lists = await Discovery(new FakeTmdb()).RecommendationsAsync([new SeriesIds(42, null)], TestContext.Current.CancellationToken);
        Assert.Empty(Assert.Single(lists));
    }

    [Fact]
    public async Task Details_include_seasons_cast_across_seasons_and_creators()
    {
        var fake = new FakeTmdb();
        var details = await Discovery(fake).DetailsAsync(1396, TestContext.Current.CancellationToken);

        Assert.Contains("append_to_response=aggregate_credits,external_ids", fake.Requests[0].Query);
        Assert.Equal(81189, details.Show.TvdbId);
        Assert.Equal("AMC", details.Show.Network);
        Assert.Equal("Ended", details.Show.Status);
        Assert.Equal("tt0903747", details.ImdbId);
        Assert.Equal([1, 0], details.Seasons.Select(s => s.SeasonNumber));
        Assert.Equal(["Bryan Cranston", "Aaron Paul"], details.Cast.Select(c => c.Name));
        Assert.Equal("Walter White / Heisenberg", details.Cast[0].Character);
        Assert.Equal(62, details.Cast[0].Episodes);
        var creator = Assert.Single(details.Crew);
        Assert.Equal("Creator, Director", creator.Jobs);
        Assert.Equal("https://image.tmdb.org/t/p/w185/v.jpg", creator.ProfileUrl);
    }

    [Fact]
    public async Task Credits_of_a_show_only_known_by_tvdb_id_are_resolved_through_tmdb()
    {
        var credits = await Discovery(new FakeTmdb()).CreditsAsync(new SeriesIds(null, 81189), TestContext.Current.CancellationToken);
        Assert.Equal(2, credits.Cast.Count);
    }
}
