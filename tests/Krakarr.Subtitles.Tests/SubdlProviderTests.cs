using System.IO.Compression;
using System.Net;
using System.Text;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Microsoft.Extensions.Configuration;

namespace Krakarr.Subtitles.Tests;

public class SubdlProviderTests
{
    private sealed class FakeApi : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url)> Requests { get; } = [];
        public string SearchJson { get; set; } = """
            {
              "status": true,
              "results": [{"sd_id": 603, "type": "movie", "name": "The Matrix", "imdb_id": "tt0133093", "tmdb_id": 603, "year": 1999}],
              "subtitles": [
                {
                  "release_name": "The.Matrix.1999.1080p.BluRay.x264-SPARKS",
                  "name": "SUBDL.com::the.matrix.1999.zip",
                  "lang": "english",
                  "language": "EN",
                  "url": "/subtitle/3197651-3213944.zip",
                  "hi": false,
                  "releases": [
                    "The.Matrix.1999.720p.BluRay.x264-SPARKS",
                    "The.Matrix.1999.1080p.BluRay.x264-SPARKS"
                  ]
                },
                {
                  "release_name": "The.Matrix.1999.720p.BRRip",
                  "name": "SUBDL.com::matrix.ptbr.zip",
                  "lang": "brazillian-portuguese",
                  "language": "BR_PT",
                  "url": "/subtitle/3197651-9999999.zip",
                  "hi": true
                }
              ]
            }
            """;
        public byte[] DownloadBytes { get; set; } = Zip(("The.Matrix.1999.1080p.BluRay.x264-SPARKS.srt", "1\n00:00:01,000 --> 00:00:02,000\nWake up, Neo...\n"));
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString()));
            if (Status != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent("""{"status":false,"error":"Invalid API key"}""", Encoding.UTF8, "application/json") });

            return Task.FromResult(request.RequestUri.AbsolutePath switch
            {
                "/api/v1/subtitles" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SearchJson, Encoding.UTF8, "application/json") },
                "/subtitle/3197651-3213944.zip" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(DownloadBytes) },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    private static SubdlProvider Provider(FakeApi api) => new(
        new Factory(api),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Subdl:BaseUrl"] = "https://subdl.test/api/v1/",
            ["Subdl:DownloadBaseUrl"] = "https://dl.subdl.test",
        }).Build());

    private static byte[] Zip(params (string Name, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return ms.ToArray();
    }

    private static readonly SubtitleSettings Settings = new()
    {
        Subdl = new SubdlSettings { Enabled = true, ApiKey = "subdl-key" },
    };

    [Fact]
    public void Builds_search_parameters_for_movies_and_episodes()
    {
        var movie = SubdlProvider.BuildSearchParameters(new SubtitleQuery
        {
            MediaType = MediaType.Movie, Title = "The Matrix", Year = 1999, ImdbId = "0133093", Languages = ["pt-br", "en", "zh-tw"],
        }, "mykey");
        Assert.Equal("api_key=mykey&imdb_id=tt0133093&languages=BR_PT%2CEN%2CZH_BG&releases=1&subs_per_page=30&type=movie", movie);

        var episode = SubdlProvider.BuildSearchParameters(new SubtitleQuery
        {
            MediaType = MediaType.Series, Title = "Dark", TmdbId = 70523, Languages = ["en"], SeasonNumber = 2, EpisodeNumber = 5,
        }, "mykey");
        Assert.Equal("api_key=mykey&episode_number=5&languages=EN&releases=1&season_number=2&subs_per_page=30&tmdb_id=70523&type=tv", episode);
    }

    [Fact]
    public async Task Searches_and_maps_candidates()
    {
        var api = new FakeApi();
        var results = await Provider(api).SearchAsync(new SubtitleQuery
        {
            MediaType = MediaType.Movie, Title = "The Matrix", ImdbId = "tt0133093", Languages = ["en", "pt-br"],
        }, Settings, TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        var en = results[0];
        Assert.Equal("subdl", en.Provider);
        Assert.Equal("/subtitle/3197651-3213944.zip", en.Payload);
        Assert.Equal("en", en.Language);
        Assert.False(en.HearingImpaired);
        Assert.True(en.MatchedById);
        Assert.Equal("The Matrix", en.FeatureTitle);
        Assert.Equal(1999, en.FeatureYear);
        Assert.Contains("The.Matrix.1999.1080p.BluRay.x264-SPARKS", en.ReleaseNames);
        Assert.Contains("The.Matrix.1999.720p.BluRay.x264-SPARKS", en.ReleaseNames);

        var ptBr = results[1];
        Assert.Equal("pt-br", ptBr.Language);
        Assert.True(ptBr.HearingImpaired);
    }

    [Fact]
    public async Task Returns_empty_when_subdl_reports_not_found()
    {
        var api = new FakeApi { SearchJson = """{"status":false,"error":"can't find movie or tv"}""" };
        var results = await Provider(api).SearchAsync(new SubtitleQuery
        {
            MediaType = MediaType.Movie, Title = "Unknown", Languages = ["en"],
        }, Settings, TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Downloads_and_extracts_matching_subtitle_from_zip()
    {
        var api = new FakeApi
        {
            DownloadBytes = Zip(
                ("readme.txt", "Downloaded from SubDL"),
                ("Dark.S01E01.1080p.NF.WEB-DL.srt", "ep1"),
                ("Dark.S01E02.1080p.NF.WEB-DL.srt", "ep2-content")),
        };
        var candidate = new SubtitleCandidate
        {
            Provider = "subdl",
            Payload = "/subtitle/3197651-3213944.zip",
            Language = "en",
            ReleaseNames = ["Dark.S01E02.1080p.NF.WEB-DL"],
            SeasonNumber = 1,
            EpisodeNumber = 2,
        };

        var content = await Provider(api).DownloadAsync(candidate, Settings, TestContext.Current.CancellationToken);

        Assert.Equal(".srt", content.Extension);
        Assert.Equal("ep2-content", Encoding.UTF8.GetString(content.Data));
        Assert.Contains(api.Requests, r => r.Url == "https://dl.subdl.test/subtitle/3197651-3213944.zip");
    }

    [Fact]
    public async Task Tests_api_key()
    {
        var api = new FakeApi();
        var msg = await Provider(api).TestAsync(Settings, TestContext.Current.CancellationToken);
        Assert.Equal("API key OK", msg);

        api.Status = HttpStatusCode.Forbidden;
        var ex = await Assert.ThrowsAsync<SubtitleProviderException>(() => Provider(api).TestAsync(Settings, TestContext.Current.CancellationToken));
        Assert.Contains("Invalid API key", ex.Message);
    }
}
