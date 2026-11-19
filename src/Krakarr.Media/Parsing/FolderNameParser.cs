using System.Globalization;
using System.Text.RegularExpressions;

namespace Krakarr.Media.Parsing;

public sealed record ParsedFolder
{
    public required string Title { get; init; }
    public int? Year { get; init; }
    public int? TmdbId { get; init; }
    public string? ImdbId { get; init; }
    public int? TvdbId { get; init; }
    public bool HasId => TmdbId is not null || ImdbId is not null || TvdbId is not null;
}

/// <summary>Reads title, year and optional ids from library folder names ("The Matrix (1999) {tmdb-603}").</summary>
public static partial class FolderNameParser
{
    public static ParsedFolder Parse(string folderName)
    {
        var name = folderName.Trim();
        int? tmdb = null, tvdb = null;
        string? imdb = null;

        // Plex / Jellyfin / Radarr id tags: {tmdb-603}, [tmdbid-603], {imdb-tt0133093}, [tvdbid-81189] ...
        foreach (Match m in IdTag().Matches(name))
        {
            var value = m.Groups["value"].Value;
            switch (m.Groups["kind"].Value.ToLowerInvariant())
            {
                case "tmdb" or "tmdbid": tmdb ??= int.Parse(value, CultureInfo.InvariantCulture); break;
                case "tvdb" or "tvdbid": tvdb ??= int.Parse(value, CultureInfo.InvariantCulture); break;
                case "imdb" or "imdbid": imdb ??= value.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? value.ToLowerInvariant() : "tt" + value; break;
            }
        }

        name = Spaces().Replace(IdTag().Replace(name, " "), " ").Trim();

        string title;
        int? year = null;
        var paren = TitleWithYear().Match(name);
        if (paren.Success)
        {
            title = paren.Groups["title"].Value;
            year = int.Parse(paren.Groups["year"].Value, CultureInfo.InvariantCulture);
        }
        else if (name.Contains('.') || name.Contains('_') || ReleaseParser.ParseQuality(name).Id != 0)
        {
            // Release-style folder: The.Matrix.1999.1080p.BluRay-GRP
            var parsed = ReleaseParser.Parse(name);
            title = parsed.Title ?? name;
            year = parsed.Year;
        }
        else
        {
            title = name;
        }

        title = Spaces().Replace(title.Replace('_', ' '), " ").Trim(' ', '-', '.');
        return new ParsedFolder { Title = title.Length > 0 ? title : folderName, Year = year, TmdbId = tmdb, ImdbId = imdb, TvdbId = tvdb };
    }

    [GeneratedRegex(@"[\{\[]\s*(?<kind>tmdbid|tmdb|tvdbid|tvdb|imdbid|imdb)\s*[-=]\s*(?<value>(?:tt)?\d+)\s*[\}\]]", RegexOptions.IgnoreCase)]
    private static partial Regex IdTag();

    [GeneratedRegex(@"^(?<title>.+?)\s*[\(\[](?<year>(?:19|20)\d{2})[\)\]]")]
    private static partial Regex TitleWithYear();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
