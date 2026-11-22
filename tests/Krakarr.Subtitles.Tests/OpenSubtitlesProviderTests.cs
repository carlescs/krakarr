using System.Globalization;
using System.Net;
using System.Text;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Krakarr.Subtitles.Tests;

public class OpenSubtitlesProviderTests
{
    private sealed class FakeApi : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body, string? Auth, string? ApiKey)> Requests { get; } = [];
        public HttpStatusCode DownloadStatus { get; set; } = HttpStatusCode.OK;
        public string DownloadErrorBody { get; set; } = """{"message":"You have downloaded your allowed 20 subtitles for 24h"}""";
        /// <summary>When set, every request is answered with 429 and this Retry-After.</summary>
        public TimeSpan? RateLimited { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, url, body, request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("Api-Key", out var k) ? k.First() : null));

            if (RateLimited is { } wait)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("""{"message":"Throttle limit reached"}""") };
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
                return limited;
            }

            var path = request.RequestUri.AbsolutePath;
            return path switch
            {
                "/api/v1/subtitles" => Json("""
                    {"total_count":2,"data":[
                      {"id":"1","type":"subtitle","attributes":{"language":"en","download_count":120,"hearing_impaired":false,
                        "ai_translated":false,"machine_translated":false,"foreign_parts_only":false,"from_trusted":true,"moviehash_match":true,
                        "release":"The.Matrix.1999.1080p.BluRay.x264-SPARKS","files":[{"file_id":4242,"file_name":"matrix.en.srt"}],
                        "feature_details":{"title":"The Matrix","year":1999}}},
                      {"id":"2","type":"subtitle","attributes":{"language":"pt-BR","download_count":5,"hearing_impaired":true,
                        "release":"The.Matrix.1999.720p","files":[{"file_id":77,"file_name":"x.srt"}]}}
                    ]}
                    """),
                "/api/v1/login" => Json("""{"token":"jwt-token","base_url":"vip-api.opensubtitles.com","user":{"allowed_downloads":100}}"""),
                "/api/v1/download" when DownloadStatus != HttpStatusCode.OK =>
                    new HttpResponseMessage(DownloadStatus) { Content = new StringContent(DownloadErrorBody) },
                "/api/v1/download" => Json("""{"link":"https://dl.example/file/4242","file_name":"matrix.en.srt","remaining":19}"""),
                "/file/4242" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("1\n00:00:01,000 --> 00:00:02,000\nHello\n"u8.ToArray()) },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage Json(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, "application/json") };
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-22T10:00:00Z", CultureInfo.InvariantCulture);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static OpenSubtitlesProvider Provider(FakeApi api, OpenSubtitlesBackoff? backoff = null, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        backoff ??= new OpenSubtitlesBackoff(time);
        // Same pipeline as SubtitlesModule registers on the named client.
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>().AddOpenSubtitlesBackoff(backoff, time).Build();
        return new(new Factory(new ResilienceHandler(pipeline) { InnerHandler = api }),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OpenSubtitles:BaseUrl"] = "https://os.test/api/v1/" }).Build(),
            backoff);
    }

    private static readonly SubtitleQuery MatrixQuery = new() { MediaType = MediaType.Movie, Title = "The Matrix", ImdbId = "tt0133093", Languages = ["en"] };
    private static readonly SubtitleSettings AnonymousSettings = new() { OpenSubtitles = new OpenSubtitlesSettings { ApiKey = "key123" } };

    [Fact]
    public async Task Stops_querying_while_rate_limited()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new Clock();
        var api = new FakeApi { RateLimited = TimeSpan.FromSeconds(30) };
        var provider = Provider(api, time: clock);

        var first = await Assert.ThrowsAsync<SubtitleProviderException>(() => provider.SearchAsync(MatrixQuery, AnonymousSettings, ct));
        Assert.Contains("rate limit", first.Message);
        Assert.Equal(clock.Now.AddSeconds(30), provider.BackoffUntil);

        // Nothing reaches the API until Retry-After has passed.
        api.RateLimited = null;
        var blocked = await Assert.ThrowsAsync<SubtitleRateLimitException>(() => provider.SearchAsync(MatrixQuery, AnonymousSettings, ct));
        Assert.Equal(clock.Now.AddSeconds(30), blocked.Until);
        await Assert.ThrowsAsync<SubtitleRateLimitException>(() =>
            provider.DownloadAsync(new SubtitleCandidate { Provider = "opensubtitles", Payload = "4242", Language = "en" }, AnonymousSettings, ct));
        Assert.Single(api.Requests);

        clock.Now = clock.Now.AddSeconds(31);
        Assert.Null(provider.BackoffUntil);
        Assert.Equal(2, (await provider.SearchAsync(MatrixQuery, AnonymousSettings, ct)).Count);
        Assert.Equal(2, api.Requests.Count);
    }

    [Fact]
    public async Task Stops_downloading_until_the_quota_renews_but_keeps_searching()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new Clock();
        var api = new FakeApi
        {
            DownloadStatus = HttpStatusCode.NotAcceptable,
            DownloadErrorBody = """{"requests":21,"remaining":0,"message":"You have downloaded your allowed 20 subtitles for 24h","reset_time_utc":"2026-11-22T14:00:00.000Z"}""",
        };
        var provider = Provider(api, time: clock);
        var candidate = new SubtitleCandidate { Provider = "opensubtitles", Payload = "4242", Language = "en" };

        var first = await Assert.ThrowsAsync<SubtitleProviderException>(() => provider.DownloadAsync(candidate, AnonymousSettings, ct));
        Assert.Contains("allowed 20 subtitles", first.Message); // the API's message survives the backoff reading the body
        Assert.Equal(DateTimeOffset.Parse("2026-11-22T14:00:00Z", CultureInfo.InvariantCulture), provider.BackoffUntil);

        api.DownloadStatus = HttpStatusCode.OK;
        var blocked = await Assert.ThrowsAsync<SubtitleRateLimitException>(() => provider.DownloadAsync(candidate, AnonymousSettings, ct));
        Assert.Contains("quota", blocked.Message);
        Assert.Single(api.Requests);

        Assert.Equal(2, (await provider.SearchAsync(MatrixQuery, AnonymousSettings, ct)).Count);

        clock.Now = DateTimeOffset.Parse("2026-11-22T14:00:01Z", CultureInfo.InvariantCulture);
        Assert.Equal(".srt", (await provider.DownloadAsync(candidate, AnonymousSettings, ct)).Extension);
    }

    private static readonly SubtitleSettings Settings = new()
    {
        OpenSubtitles = new OpenSubtitlesSettings { ApiKey = "key123", Username = "user-" + Guid.NewGuid(), Password = "pw" },
    };

    [Fact]
    public void Builds_sorted_lowercase_parameters()
    {
        var movie = OpenSubtitlesProvider.BuildSearchParameters(new SubtitleQuery
        {
            MediaType = MediaType.Movie, Title = "The Matrix", Year = 1999, ImdbId = "tt0133093", Languages = ["es", "EN"], FileHash = "ABCDEF0123456789",
        });
        Assert.Equal("imdb_id=133093&languages=en%2Ces&moviehash=abcdef0123456789&type=movie", movie);

        var episode = OpenSubtitlesProvider.BuildSearchParameters(new SubtitleQuery
        {
            MediaType = MediaType.Series, Title = "Some Show", Languages = ["en"], SeasonNumber = 2, EpisodeNumber = 5,
        });
        Assert.Equal("episode_number=5&languages=en&query=some%20show&season_number=2&type=episode", episode);
    }

    [Fact]
    public async Task Searches_and_maps_candidates()
    {
        var api = new FakeApi();
        var results = await Provider(api).SearchAsync(new SubtitleQuery { MediaType = MediaType.Movie, Title = "The Matrix", ImdbId = "tt0133093", Languages = ["en", "pt-br"] },
            Settings, TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        var en = results[0];
        Assert.Equal("4242", en.Payload);
        Assert.Equal("en", en.Language);
        Assert.True(en.HashMatch);
        Assert.True(en.TrustedUploader);
        Assert.True(en.MatchedById);
        Assert.Equal(1999, en.FeatureYear);
        Assert.Equal("pt-br", results[1].Language);
        Assert.True(results[1].HearingImpaired);
        Assert.Equal("key123", api.Requests[0].ApiKey);
    }

    [Fact]
    public async Task Downloads_with_login_token()
    {
        var api = new FakeApi();
        var candidate = new SubtitleCandidate { Provider = "opensubtitles", Payload = "4242", Language = "en" };
        var content = await Provider(api).DownloadAsync(candidate, Settings, TestContext.Current.CancellationToken);

        Assert.Equal(".srt", content.Extension);
        Assert.Contains("Hello", Encoding.UTF8.GetString(content.Data));
        var download = api.Requests.Single(r => r.Url.EndsWith("/download", StringComparison.Ordinal));
        Assert.Equal("jwt-token", download.Auth);
        Assert.Contains("\"file_id\":4242", download.Body);
    }

    [Fact]
    public async Task Reports_quota_errors()
    {
        var api = new FakeApi { DownloadStatus = HttpStatusCode.NotAcceptable };
        var candidate = new SubtitleCandidate { Provider = "opensubtitles", Payload = "4242", Language = "en" };
        var ex = await Assert.ThrowsAsync<SubtitleProviderException>(() =>
            Provider(api).DownloadAsync(candidate, Settings with { OpenSubtitles = Settings.OpenSubtitles with { Username = null } }, TestContext.Current.CancellationToken));
        Assert.Contains("quota", ex.Message);
    }

    [Theory]
    [InlineData("tt0133093", "133093")]
    [InlineData("133093", "133093")]
    [InlineData("", null)]
    [InlineData("nm123", null)]
    public void Strips_imdb_ids(string input, string? expected) => Assert.Equal(expected, OpenSubtitlesProvider.ImdbNumber(input));
}
