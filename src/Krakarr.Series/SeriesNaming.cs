using System.Globalization;
using System.Text.RegularExpressions;
using Krakarr.Core.Domain;
using Krakarr.Media.Files;
using Krakarr.Media.Parsing;

namespace Krakarr.Series;

public static partial class SeriesNaming
{
    /// <summary>Series title without a trailing disambiguation year, e.g. "Doctor Who (2005)" → "Doctor Who".</summary>
    public static string BaseTitle(string title) => TrailingYear().Replace(title, string.Empty).Trim();

    public static string NormalizedTitle(string title) => ReleaseParser.NormalizeTitle(BaseTitle(title));

    public static string EpisodeCode(int season, IEnumerable<int> episodes) =>
        $"S{season:00}" + string.Concat(episodes.Order().Select(e => $"E{e:00}"));

    public static string BuildSeriesFolder(Core.Domain.Series series, string format) =>
        FileNameBuilder.Build(format, SeriesTokens(series));

    public static string BuildSeasonFolder(int season, string format) =>
        season == 0 ? "Specials" : FileNameBuilder.Build(format, new Dictionary<string, string?> { ["Season"] = Two(season) });

    public static string BuildEpisodeFileName(Core.Domain.Series series, IReadOnlyList<Episode> episodes, Quality quality, string? releaseGroup, string format, string extension)
    {
        var ordered = episodes.OrderBy(e => e.EpisodeNumber).ToList();
        var tokens = SeriesTokens(series);
        tokens["Season"] = Two(ordered[0].SeasonNumber);
        tokens["Episode"] = string.Join("-E", ordered.Select(e => Two(e.EpisodeNumber)));
        tokens["Episode Title"] = string.Join(" + ", ordered.Select(e => e.Title).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct());
        tokens["Quality"] = quality.Name;
        tokens["Release Group"] = releaseGroup;
        return FileNameBuilder.Build(format, tokens) + extension.ToLowerInvariant();
    }

    private static Dictionary<string, string?> SeriesTokens(Core.Domain.Series s) => new()
    {
        ["Series Title"] = s.Title,
        ["Series CleanTitle"] = new string(s.Title.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()),
        ["Series Year"] = s.Year > 0 && !TrailingYear().IsMatch(s.Title) ? s.Year.ToString(CultureInfo.InvariantCulture) : null,
        ["TvdbId"] = s.TvdbId.ToString(CultureInfo.InvariantCulture),
        ["ImdbId"] = s.ImdbId,
    };

    private static string Two(int n) => n.ToString("00", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\s*\((?:19|20)\d{2}\)\s*$")]
    private static partial Regex TrailingYear();
}
