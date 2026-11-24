using System.Net;
using System.Text;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Metadata;
using Krakarr.Data;
using Microsoft.Extensions.Configuration;

namespace Krakarr.Movies.Tests;

public class RecommendationRankerTests
{
    [Fact]
    public void Titles_suggested_by_several_seeds_rank_first_and_owned_titles_are_dropped()
    {
        IReadOnlyList<int>[] lists =
        [
            [10, 20, 30],
            [30, 40, 10, 10],
            [50, 30],
        ];

        var ranked = RecommendationRanker.Rank(lists, x => x, new HashSet<int> { 20 }, take: 10);

        Assert.Equal([30, 10, 50, 40], ranked);
    }

    [Fact]
    public void Earlier_positions_win_ties_and_take_limits_the_result()
    {
        IReadOnlyList<int>[] lists = [[1, 2], [3, 4]];
        Assert.Equal([1, 3], RecommendationRanker.Rank(lists, x => x, new HashSet<int>(), take: 2));
    }
}

public class TmdbMovieDiscoveryTests
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
                "/3/trending/movie/week" or "/3/discover/movie" => Json("""
                    {"page":2,"total_pages":900,"results":[{"id":603,"title":"The Matrix","release_date":"1999-03-30","poster_path":"/m.jpg","vote_average":8.217}]}
                    """),
                "/3/movie/603" => Json("""
                    {"id":603,"title":"The Matrix","release_date":"1999-03-30","tagline":"Welcome to the Real World.","genres":[{"name":"Action"},{"name":"Science Fiction"}],
                     "release_dates":{"results":[
                       {"iso_3166_1":"US","release_dates":[{"release_date":"1999-03-31T00:00:00.000Z","type":3},{"release_date":"2007-05-22T00:00:00.000Z","type":5}]},
                       {"iso_3166_1":"ES","release_dates":[{"release_date":"1999-06-23T00:00:00.000Z","type":3}]}]},
                     "credits":{"cast":[{"id":6384,"name":"Keanu Reeves","character":"Neo","order":0,"profile_path":"/k.jpg"},{"id":0,"name":"Nobody","order":1}],
                      "crew":[{"id":9339,"name":"Lilly Wachowski","job":"Director"},{"id":9339,"name":"Lilly Wachowski","job":"Screenplay"},{"id":7,"name":"Best Boy","job":"Best Boy Electric"}]}}
                    """),
                "/3/movie/603/recommendations" => Json("""{"page":1,"total_pages":1,"results":[{"id":604,"title":"The Matrix Reloaded","release_date":"2003-05-15"}]}"""),
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

    private sealed class Settings(string? releaseRegion = null) : ISettingsService
    {
        public Task<T> GetAsync<T>(CancellationToken ct = default) where T : class, ISettingsSection, new() =>
            Task.FromResult(new MetadataSettings { TmdbApiKey = "v3key", ReleaseRegion = releaseRegion } as T ?? new T());
        public Task SaveAsync<T>(T value, CancellationToken ct = default) where T : class, ISettingsSection, new() => Task.CompletedTask;
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    }

    private static TmdbMovieDiscovery Discovery(FakeTmdb fake, string? releaseRegion = null) =>
        new(new TmdbApi(new Factory(fake), new ConfigurationBuilder().Build()), new Settings(releaseRegion), new FixedTime());

    [Fact]
    public async Task Reads_a_trending_page_with_ratings()
    {
        var fake = new FakeTmdb();
        var page = await Discovery(fake).ListAsync(MovieList.Trending, 2, TestContext.Current.CancellationToken);

        var movie = Assert.Single(page.Results);
        Assert.Equal(603, movie.TmdbId);
        Assert.Equal(1999, movie.Year);
        Assert.Equal(8.2, movie.Rating);
        Assert.Equal(2, page.Page);
        Assert.Equal(500, page.TotalPages);
        Assert.Contains("page=2", fake.Requests[0].Query);
    }

    [Fact]
    public async Task Upcoming_starts_today()
    {
        var fake = new FakeTmdb();
        await Discovery(fake).ListAsync(MovieList.Upcoming, 1, TestContext.Current.CancellationToken);
        Assert.Contains("primary_release_date.gte=2026-11-20", Uri.UnescapeDataString(fake.Requests[0].Query));
    }

    [Fact]
    public async Task Keyword_discovery_requires_every_keyword()
    {
        var fake = new FakeTmdb();
        var page = await Discovery(fake).KeywordsAsync([4379, 9882], 3, TestContext.Current.CancellationToken);

        Assert.Equal(603, Assert.Single(page.Results).TmdbId);
        Assert.Equal("/3/discover/movie", fake.Requests[0].AbsolutePath);
        var query = Uri.UnescapeDataString(fake.Requests[0].Query);
        Assert.Contains("with_keywords=4379,9882", query);
        Assert.Contains("page=3", query);
        Assert.Contains("include_adult=false", query);
    }

    [Fact]
    public async Task Batch_recommendations_skip_unknown_titles()
    {
        var lists = await Discovery(new FakeTmdb()).RecommendationsAsync([603, 999], TestContext.Current.CancellationToken);
        Assert.Equal(604, Assert.Single(lists[0]).TmdbId);
        Assert.Empty(lists[1]);
    }

    [Fact]
    public async Task Details_include_genres_billed_cast_key_crew_and_release_dates()
    {
        var fake = new FakeTmdb();
        var details = await Discovery(fake, "es").DetailsAsync(603, TestContext.Current.CancellationToken);

        Assert.Contains("append_to_response=credits,release_dates", fake.Requests[0].Query);
        Assert.Equal("The Matrix", details.Movie.Title);
        Assert.Equal(new DateOnly(1999, 3, 30), details.ReleaseDate);
        Assert.Equal("ES", details.ReleaseRegion);
        Assert.Equal(3, details.Releases.Count);
        var theatrical = details.Releases.Single(r => r.Type == ReleaseDateType.Theatrical);
        Assert.Equal(new ReleaseDateDto(new DateOnly(1999, 6, 23), "ES"), theatrical.Region);
        Assert.Equal(new ReleaseDateDto(new DateOnly(1999, 3, 31), "US"), theatrical.Earliest);
        var digital = details.Releases.Single(r => r.Type == ReleaseDateType.Digital);
        Assert.Null(digital.Region);
        Assert.Null(digital.Earliest);
        var physical = details.Releases.Single(r => r.Type == ReleaseDateType.Physical);
        Assert.Null(physical.Region);
        Assert.Equal(new ReleaseDateDto(new DateOnly(2007, 5, 22), "US"), physical.Earliest);

        Assert.Equal(["Action", "Science Fiction"], details.Genres);
        var neo = Assert.Single(details.Cast);
        Assert.Equal("Neo", neo.Character);
        Assert.Equal("https://image.tmdb.org/t/p/w185/k.jpg", neo.ProfileUrl);
        var director = Assert.Single(details.Crew);
        Assert.Equal("Director, Screenplay", director.Jobs);
    }
}

public class TmdbKeywordsTests
{
    private static TmdbKeywords Keywords(FakeTmdbKeywords fake) => new(new TmdbApi(new Factory(fake), new ConfigurationBuilder().Build()));
    private static readonly MetadataSettings Meta = new() { TmdbApiKey = "v3key" };

    [Fact]
    public async Task Search_puts_exact_and_prefix_matches_first_and_drops_blank_names()
    {
        var results = await Keywords(new FakeTmdbKeywords()).SearchAsync(Meta, " heist ", TestContext.Current.CancellationToken);

        Assert.Equal(["heist", "heist movie", "bank heist"], results.Select(k => k.Name));
        Assert.Equal(10051, results[0].Id);
    }

    [Fact]
    public async Task Blank_search_asks_nothing()
    {
        var fake = new FakeTmdbKeywords();
        Assert.Empty(await Keywords(fake).SearchAsync(Meta, "  ", TestContext.Current.CancellationToken));
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task Resolves_a_keyword_name_by_id() =>
        Assert.Equal(new KeywordLookup(10051, "heist"), await Keywords(new FakeTmdbKeywords()).GetAsync(Meta, 10051, TestContext.Current.CancellationToken));

    [Theory]
    [InlineData(null, new int[0])]
    [InlineData("", new int[0])]
    [InlineData("4379, 9882,4379,x,-3,0", new[] { 4379, 9882 })]
    [InlineData("1,2,3,4,5,6,7,8,9,10,11,12", new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })]
    public void Keyword_ids_are_distinct_positive_and_capped(string? value, int[] expected) =>
        Assert.Equal(expected, KeywordIds.Parse(value));

    private sealed class FakeTmdbKeywords : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var json = request.RequestUri!.AbsolutePath.Contains("search/keyword")
                ? """
                  {"page":1,"total_pages":1,"results":[
                    {"id":1,"name":"bank heist"},{"id":2,"name":"heist movie"},{"id":3,"name":" "},{"id":10051,"name":"heist"},{"id":2,"name":"heist movie"}]}
                  """
                : """{"id":10051,"name":"heist"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

public class TmdbPeopleTests
{
    [Fact]
    public async Task Filmography_merges_roles_drops_talk_shows_and_lists_newest_first()
    {
        var fake = new FakeTmdbPeople();
        var people = new TmdbPeople(new TmdbApi(new Factory(fake), new ConfigurationBuilder().Build()));
        var person = await people.GetPersonAsync(new MetadataSettings { TmdbApiKey = "v3key" }, 6384, TestContext.Current.CancellationToken);

        Assert.Equal("Keanu Reeves", person.Name);
        Assert.Equal(new DateOnly(1964, 9, 2), person.Birthday);
        Assert.Equal(
            ["Untitled Project", "Swedish Dicks", "Man of Tai Chi", "The Matrix"],
            person.Credits.Select(c => c.Title));
        var matrix = person.Credits.Single(c => c.TmdbId == 603);
        Assert.Equal("Neo, Thomas Anderson", matrix.Role);
        Assert.Equal("Acting", matrix.Department);
        Assert.Equal("series", person.Credits.Single(c => c.TmdbId == 4321).Kind);
        Assert.Equal("Directing", person.Credits.Single(c => c.TmdbId == 5555).Department);
    }

    [Fact]
    public async Task Search_maps_department_profile_and_known_for_titles()
    {
        var fake = new FakeTmdbPeople();
        var people = new TmdbPeople(new TmdbApi(new Factory(fake), new ConfigurationBuilder().Build()));
        var results = await people.SearchAsync(new MetadataSettings { TmdbApiKey = "v3key" }, " keanu ", TestContext.Current.CancellationToken);

        var keanu = Assert.Single(results);
        Assert.Equal(6384, keanu.TmdbId);
        Assert.Equal("Keanu Reeves", keanu.Name);
        Assert.Equal("Acting", keanu.KnownForDepartment);
        Assert.Equal("https://image.tmdb.org/t/p/w185/k.jpg", keanu.ProfileUrl);
        Assert.Equal(["The Matrix", "John Wick", "Swedish Dicks"], keanu.KnownFor);
    }

    private sealed class FakeTmdbPeople : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath.Contains("search/person")
                ? """
                  {"results":[
                    {"id":6384,"name":"Keanu Reeves","known_for_department":"Acting","profile_path":"/k.jpg",
                     "known_for":[
                       {"id":603,"media_type":"movie","title":"The Matrix"},
                       {"id":245891,"media_type":"movie","title":"John Wick"},
                       {"id":245891,"media_type":"movie","title":"John Wick"},
                       {"id":4321,"media_type":"tv","name":"Swedish Dicks"}]}]}
                  """
                : """
                  {"id":6384,"name":"Keanu Reeves","birthday":"1964-09-02","combined_credits":{
                    "cast":[
                      {"id":603,"media_type":"movie","title":"The Matrix","release_date":"1999-03-30","character":"Neo","popularity":80},
                      {"id":603,"media_type":"movie","title":"The Matrix","release_date":"1999-03-30","character":"Thomas Anderson","popularity":80},
                      {"id":99,"media_type":"movie","title":"Untitled Project","release_date":"","popularity":1},
                      {"id":1234,"media_type":"tv","name":"Late Night Talk","first_air_date":"2015-01-01","genre_ids":[10767],"character":"Self"},
                      {"id":4321,"media_type":"tv","name":"Swedish Dicks","first_air_date":"2016-08-01","genre_ids":[35],"character":"Tex"}],
                    "crew":[{"id":5555,"media_type":"movie","title":"Man of Tai Chi","release_date":"2013-07-05","department":"Directing","job":"Director"}]}}
                  """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
