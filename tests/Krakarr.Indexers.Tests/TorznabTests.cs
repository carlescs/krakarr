using Krakarr.Core.Domain;

namespace Krakarr.Indexers.Tests;

public class TorznabTests
{
    private static readonly Indexer Indexer = new() { Id = 7, Name = "Test", BaseUrl = "http://localhost:9117/api/v2.0/indexers/test/results/torznab", ApiKey = "abc", Priority = 10 };

    private const string ResultsXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:atom="http://www.w3.org/2005/Atom" xmlns:torznab="http://torznab.com/schemas/2015/feed">
          <channel>
            <title>Test</title>
            <item>
              <title>The.Matrix.1999.1080p.BluRay.x264-SPARKS</title>
              <guid>https://tracker/details/1</guid>
              <link>https://tracker/download/1.torrent</link>
              <comments>https://tracker/details/1</comments>
              <pubDate>Sat, 01 Jun 2024 12:00:00 +0000</pubDate>
              <size>8589934592</size>
              <enclosure url="https://tracker/download/1.torrent" length="8589934592" type="application/x-bittorrent" />
              <torznab:attr name="category" value="2000" />
              <torznab:attr name="category" value="2040" />
              <torznab:attr name="seeders" value="42" />
              <torznab:attr name="peers" value="50" />
              <torznab:attr name="imdbid" value="tt0133093" />
              <torznab:attr name="infohash" value="ABCDEF0123456789ABCDEF0123456789ABCDEF01" />
            </item>
            <item>
              <title>Some Magnet Release</title>
              <guid>magnet-guid</guid>
              <link>magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567</link>
              <torznab:attr name="seeders" value="0" />
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public void Parses_results()
    {
        var results = TorznabParser.ParseResults(ResultsXml, Indexer);
        Assert.Equal(2, results.Count);

        var first = results[0];
        Assert.Equal("The.Matrix.1999.1080p.BluRay.x264-SPARKS", first.Title);
        Assert.Equal("https://tracker/download/1.torrent", first.DownloadUrl);
        Assert.Equal(8589934592, first.Size);
        Assert.Equal(42, first.Seeders);
        Assert.Equal(133093, first.ImdbId);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef01", first.InfoHash);
        Assert.Equal([2000, 2040], first.Categories);
        Assert.Equal("Test", first.IndexerName);
        Assert.Equal(10, first.IndexerPriority);
        Assert.Equal(new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero), first.PublishDate);

        var second = results[1];
        Assert.Null(second.DownloadUrl);
        Assert.StartsWith("magnet:", second.MagnetUrl);
        Assert.Equal(0, second.Seeders);
    }

    [Fact]
    public void Throws_on_error_document()
    {
        var ex = Assert.Throws<IndexerException>(() =>
            TorznabParser.ParseResults("""<?xml version="1.0"?><error code="100" description="Invalid API Key" />""", Indexer));
        Assert.Contains("Invalid API Key", ex.Message);
    }

    [Fact]
    public void Parses_capabilities()
    {
        var caps = TorznabParser.ParseCapabilities("""
            <caps>
              <searching>
                <search available="yes" supportedParams="q" />
                <tv-search available="yes" supportedParams="q,season,ep,tvdbid" />
                <movie-search available="yes" supportedParams="q,imdbid" />
              </searching>
              <categories>
                <category id="2000" name="Movies"><subcat id="2040" name="Movies/HD" /></category>
                <category id="5000" name="TV" />
              </categories>
            </caps>
            """);

        Assert.True(caps.MovieSearchAvailable);
        Assert.Contains("imdbid", caps.MovieSearchParams);
        Assert.Contains("tvdbid", caps.TvSearchParams);
        Assert.Equal(3, caps.Categories.Count);
        Assert.Equal(new IndexerCategory(2000, "Movies"), caps.Categories[0]);
        Assert.Equal(new IndexerCategory(2040, "Movies/HD", 2000), caps.Categories[1]);
        Assert.Equal(new IndexerCategory(5000, "TV"), caps.Categories[2]);
    }

    [Fact]
    public void Builds_movie_search_using_imdb_when_supported()
    {
        var caps = new IndexerCapabilities { MovieSearchAvailable = true, MovieSearchParams = new HashSet<string> { "q", "imdbid" } };
        var p = TorznabClient.BuildSearchParameters(Indexer, new SearchCriteria { Type = SearchType.Movie, Query = "The Matrix 1999", ImdbId = "tt0133093" }, caps);

        Assert.Contains(("t", "movie"), p);
        Assert.Contains(("imdbid", "0133093"), p);
        Assert.Contains(("cat", "2000"), p);
        Assert.DoesNotContain(p, x => x.Key == "q");
    }

    [Fact]
    public void Falls_back_to_text_search_without_caps()
    {
        var p = TorznabClient.BuildSearchParameters(Indexer, new SearchCriteria { Type = SearchType.Movie, Query = "The Matrix 1999", ImdbId = "tt0133093" }, new IndexerCapabilities());
        Assert.Contains(("t", "search"), p);
        Assert.Contains(("q", "The Matrix 1999"), p);
    }

    [Fact]
    public void Builds_url_with_api_key()
    {
        var url = TorznabClient.BuildUrl(Indexer, [("t", "caps")]);
        Assert.Equal("http://localhost:9117/api/v2.0/indexers/test/results/torznab/api?t=caps&apikey=abc", url.ToString());
    }

    [Fact]
    public void Builds_tv_search_with_tvdb_id_season_and_episode()
    {
        var caps = new IndexerCapabilities { TvSearchAvailable = true, TvSearchParams = new HashSet<string> { "q", "season", "ep", "tvdbid" } };
        var p = TorznabClient.BuildSearchParameters(Indexer, new SearchCriteria { Type = SearchType.Tv, Query = "The Office", TvdbId = 73244, Season = 5, Episode = 14 }, caps);
        Assert.Contains(("t", "tvsearch"), p);
        Assert.Contains(("tvdbid", "73244"), p);
        Assert.Contains(("season", "5"), p);
        Assert.Contains(("ep", "14"), p);
        Assert.Contains(("cat", "5000"), p);
    }

    [Fact]
    public void Tv_search_without_caps_puts_episode_in_query()
    {
        var p = TorznabClient.BuildSearchParameters(Indexer, new SearchCriteria { Type = SearchType.Tv, Query = "The Office", Season = 5, Episode = 14 }, new IndexerCapabilities());
        Assert.Contains(("q", "The Office S05E14"), p);
        var season = TorznabClient.BuildSearchParameters(Indexer, new SearchCriteria { Type = SearchType.Tv, Query = "The Office", Season = 5 }, new IndexerCapabilities());
        Assert.Contains(("q", "The Office S05"), season);
    }
}
