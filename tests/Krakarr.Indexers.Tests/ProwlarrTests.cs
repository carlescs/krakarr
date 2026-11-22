using System.Text;

namespace Krakarr.Indexers.Tests;

public class ProwlarrTests
{
    private const string IndexersJson = """
        [
          {
            "id": 3, "name": "Nyaa", "protocol": "torrent", "enable": true, "priority": 10, "supportsRss": true,
            "fields": [{ "name": "baseUrl", "value": "https://nyaa.si" }],
            "capabilities": { "categories": [
              { "id": 2000, "name": "Movies", "subCategories": [{ "id": 2040, "name": "Movies/HD" }] },
              { "id": 5000, "name": "TV", "subCategories": [{ "id": 5070, "name": "TV/Anime" }] }
            ] }
          },
          { "id": 7, "name": "Alpha Usenet", "protocol": "usenet", "enable": false, "priority": 0, "supportsRss": false }
        ]
        """;

    [Fact]
    public async Task Parses_the_indexer_list()
    {
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes(IndexersJson));
        var indexers = await ProwlarrClient.ParseAsync(body, TestContext.Current.CancellationToken);

        Assert.Equal(["Alpha Usenet", "Nyaa"], indexers.Select(i => i.Name));
        var usenet = indexers[0];
        Assert.False(usenet.Supported);
        Assert.Equal(25, usenet.Priority);
        Assert.Empty(usenet.Categories);

        var nyaa = indexers[1];
        Assert.True(nyaa.Supported);
        Assert.True(nyaa.Enabled);
        Assert.Equal(10, nyaa.Priority);
        Assert.Equal([2000, 2040, 5000, 5070], nyaa.Categories.Select(c => c.Id));
        Assert.Equal(2000, nyaa.Categories[1].ParentId);
    }

    [Fact]
    public void Suggests_top_level_categories_with_a_fallback()
    {
        IReadOnlyList<IndexerCategory> anime = [new(5070, "TV/Anime", 5000), new(5080, "TV/Documentary", 5000)];
        Assert.Equal([5070, 5080], ProwlarrClient.SuggestCategories(anime, 5000, 5999, 5000));
        Assert.Equal([2000], ProwlarrClient.SuggestCategories(anime, 2000, 2999, 2000));

        IReadOnlyList<IndexerCategory> full = [new(2000, "Movies"), new(2040, "Movies/HD", 2000)];
        Assert.Equal([2000], ProwlarrClient.SuggestCategories(full, 2000, 2999, 2000));
    }

    [Fact]
    public void Builds_the_feed_url()
    {
        Assert.Equal("http://prowlarr:9696/3", ProwlarrClient.FeedUrl(" http://prowlarr:9696/ ", 3));
        Assert.Equal("http://host/prowlarr/12", ProwlarrClient.FeedUrl("http://host/prowlarr", 12));
    }

    [Fact]
    public void Avoids_name_clashes()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Nyaa", "nyaa (Prowlarr)" };
        Assert.Equal("Other", IndexerEndpoints.UniqueName("Other", taken));
        Assert.Equal("Nyaa (Prowlarr 2)", IndexerEndpoints.UniqueName("Nyaa", taken));
    }
}
