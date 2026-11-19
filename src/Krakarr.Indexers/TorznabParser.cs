using System.Globalization;
using System.Xml.Linq;
using Krakarr.Core.Domain;

namespace Krakarr.Indexers;

public sealed class IndexerException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Parses Torznab / Newznab RSS responses and capability documents.</summary>
public static class TorznabParser
{
    private static readonly XNamespace TorznabNs = "http://torznab.com/schemas/2015/feed";
    private static readonly XNamespace NewznabNs = "http://www.newznab.com/DTD/2010/feeds/attributes/";

    public static IReadOnlyList<ReleaseInfo> ParseResults(string xml, Indexer indexer)
    {
        var doc = Load(xml);
        ThrowIfError(doc);

        var channel = doc.Root?.Element("channel");
        if (channel is null)
        {
            return [];
        }

        var results = new List<ReleaseInfo>();
        foreach (var item in channel.Elements("item"))
        {
            var attrs = ReadAttributes(item);
            var title = item.Element("title")?.Value.Trim();
            if (string.IsNullOrEmpty(title))
            {
                continue;
            }

            var enclosure = item.Element("enclosure");
            var link = item.Element("link")?.Value.Trim();
            var enclosureUrl = enclosure?.Attribute("url")?.Value;
            var downloadUrl = !string.IsNullOrEmpty(enclosureUrl) ? enclosureUrl : link;

            var magnet = attrs.GetValueOrDefault("magneturl");
            if (magnet is null && downloadUrl?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true)
            {
                magnet = downloadUrl;
                downloadUrl = null;
            }

            var size = ParseLong(item.Element("size")?.Value)
                ?? ParseLong(attrs.GetValueOrDefault("size"))
                ?? ParseLong(enclosure?.Attribute("length")?.Value)
                ?? 0;

            var seeders = ParseInt(attrs.GetValueOrDefault("seeders"));
            var peers = ParseInt(attrs.GetValueOrDefault("peers"));

            results.Add(new ReleaseInfo
            {
                Title = title,
                Guid = item.Element("guid")?.Value ?? downloadUrl ?? magnet ?? title,
                DownloadUrl = downloadUrl,
                MagnetUrl = magnet,
                InfoHash = attrs.GetValueOrDefault("infohash")?.ToLowerInvariant(),
                InfoUrl = item.Element("comments")?.Value ?? (link != downloadUrl ? link : null),
                Size = size,
                Seeders = seeders,
                Peers = peers,
                PublishDate = ParseDate(item.Element("pubDate")?.Value),
                ImdbId = ParseInt(attrs.GetValueOrDefault("imdb")?.TrimStart('t') ?? attrs.GetValueOrDefault("imdbid")?.TrimStart('t')),
                TmdbId = ParseInt(attrs.GetValueOrDefault("tmdbid")),
                TvdbId = ParseInt(attrs.GetValueOrDefault("tvdbid")),
                Categories = item.Elements(TorznabNs + "attr").Concat(item.Elements(NewznabNs + "attr"))
                    .Where(a => (string?)a.Attribute("name") == "category")
                    .Select(a => ParseInt((string?)a.Attribute("value")))
                    .Concat(item.Elements("category").Select(c => ParseInt(c.Value)))
                    .OfType<int>()
                    .Distinct()
                    .ToList(),
                IndexerId = indexer.Id,
                IndexerName = indexer.Name,
                IndexerPriority = indexer.Priority,
            });
        }

        return results;
    }

    public static IndexerCapabilities ParseCapabilities(string xml)
    {
        var doc = Load(xml);
        ThrowIfError(doc);

        var root = doc.Root ?? throw new IndexerException("Empty capabilities document");
        var searching = root.Element("searching");

        static (bool Available, HashSet<string> Params) ReadMode(XElement? el)
        {
            if (el is null || !string.Equals((string?)el.Attribute("available"), "yes", StringComparison.OrdinalIgnoreCase))
            {
                return (false, []);
            }

            var p = ((string?)el.Attribute("supportedParams") ?? "q")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant())
                .ToHashSet();
            return (true, p);
        }

        var search = ReadMode(searching?.Element("search"));
        var movie = ReadMode(searching?.Element("movie-search"));
        var tv = ReadMode(searching?.Element("tv-search"));

        var categories = root.Element("categories")?.Elements("category")
            .SelectMany(c => new[] { c }.Concat(c.Elements("subcat")))
            .Select(c => new IndexerCategory(ParseInt((string?)c.Attribute("id")) ?? 0, (string?)c.Attribute("name") ?? string.Empty))
            .Where(c => c.Id > 0)
            .ToList() ?? [];

        return new IndexerCapabilities
        {
            SearchAvailable = search.Available,
            MovieSearchAvailable = movie.Available,
            MovieSearchParams = movie.Params,
            TvSearchAvailable = tv.Available,
            TvSearchParams = tv.Params,
            Categories = categories,
        };
    }

    private static XDocument Load(string xml)
    {
        try
        {
            return XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new IndexerException("Indexer returned invalid XML", ex);
        }
    }

    private static void ThrowIfError(XDocument doc)
    {
        if (doc.Root?.Name.LocalName == "error")
        {
            var code = (string?)doc.Root.Attribute("code");
            var description = (string?)doc.Root.Attribute("description");
            throw new IndexerException($"Indexer error {code}: {description}");
        }
    }

    private static Dictionary<string, string> ReadAttributes(XElement item)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in item.Elements(TorznabNs + "attr").Concat(item.Elements(NewznabNs + "attr")))
        {
            var name = (string?)a.Attribute("name");
            var value = (string?)a.Attribute("value");
            if (name is not null && value is not null)
            {
                dict.TryAdd(name, value);
            }
        }

        return dict;
    }

    private static long? ParseLong(string? s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static int? ParseInt(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static DateTimeOffset? ParseDate(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}

public sealed record IndexerCategory(int Id, string Name);

public sealed record IndexerCapabilities
{
    public bool SearchAvailable { get; init; } = true;
    public bool MovieSearchAvailable { get; init; }
    public IReadOnlySet<string> MovieSearchParams { get; init; } = new HashSet<string>();
    public bool TvSearchAvailable { get; init; }
    public IReadOnlySet<string> TvSearchParams { get; init; } = new HashSet<string>();
    public IReadOnlyList<IndexerCategory> Categories { get; init; } = [];
}
