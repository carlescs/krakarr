using System.Net;
using System.Text;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Microsoft.Extensions.Configuration;

namespace Krakarr.Subtitles.Tests;

public class OpenSubtitlesProviderTests
{
    private sealed class FakeApi : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body, string? Auth, string? ApiKey)> Requests { get; } = [];
        public HttpStatusCode DownloadStatus { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, url, body, request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("Api-Key", out var k) ? k.First() : null));

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
                    new HttpResponseMessage(DownloadStatus) { Content = new StringContent("""{"message":"You have downloaded your allowed 20 subtitles for 24h"}""") },
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

    private static OpenSubtitlesProvider Provider(FakeApi api) => new(new Factory(api),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OpenSubtitles:BaseUrl"] = "https://os.test/api/v1/" }).Build());

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
