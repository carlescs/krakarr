using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Krakarr.Core.Domain;

namespace Krakarr.Media.Parsing;

/// <summary>Parses scene/p2p release names into title, year, quality, group and episode info.</summary>
public static partial class ReleaseParser
{
    private static readonly string[] VideoExtensions = [".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts", ".webm", ".mpg", ".mpeg"];

    public static ParsedRelease Parse(string releaseTitle)
    {
        var title = LeadingTag().Replace(StripExtension(releaseTitle.Trim()), string.Empty);
        var normalized = Separators().Replace(title, " ").Trim();

        var (season, episodes) = ParseEpisodes(normalized);
        var (mediaTitle, year) = season is not null ? ParseSeriesTitle(normalized) : ParseMovieTitle(normalized);

        return new ParsedRelease
        {
            ReleaseTitle = releaseTitle,
            Title = mediaTitle,
            Year = year,
            Quality = ParseQuality(normalized),
            IsProper = ProperRegex().IsMatch(normalized),
            IsRepack = RepackRegex().IsMatch(normalized),
            ReleaseGroup = ParseReleaseGroup(title),
            SeasonNumber = season,
            EpisodeNumbers = episodes,
        };
    }

    public static bool IsVideoFile(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static Quality ParseQuality(string name)
    {
        var text = Separators().Replace(StripExtension(name), " ");
        var resolution = ParseResolution(text);
        var source = ParseSource(text);
        return Qualities.Find(source, resolution);
    }

    /// <summary>Lower-case, accent-free, alphanumeric-only form used to compare titles.</summary>
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var decomposed = title.Replace("&", " and ").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        var result = sb.ToString();
        return result.StartsWith("the", StringComparison.Ordinal) && result.Length > 6 ? result[3..] : result;
    }

    private static string StripExtension(string title)
    {
        var ext = Path.GetExtension(title);
        return VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) || ext.Equals(".torrent", StringComparison.OrdinalIgnoreCase)
            ? title[..^ext.Length]
            : title;
    }

    private static (string? Title, int? Year) ParseMovieTitle(string text)
    {
        // Prefer the last plausible year so titles like "2001 A Space Odyssey 1968" work.
        var matches = YearRegex().Matches(text);
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var m = matches[i];
            if (m.Index == 0)
            {
                continue;
            }

            var candidate = CleanTitle(text[..m.Index]);
            if (candidate.Length > 0)
            {
                return (candidate, int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture));
            }
        }

        // No year: title is everything up to the first quality/technical token.
        var tokenMatch = TechnicalTokenRegex().Match(text);
        var titlePart = tokenMatch.Success && tokenMatch.Index > 0 ? text[..tokenMatch.Index] : text;
        var cleaned = CleanTitle(titlePart);
        return (cleaned.Length > 0 ? cleaned : null, null);
    }

    private static (string? Title, int? Year) ParseSeriesTitle(string text)
    {
        var m = EpisodeRegex().Match(text);
        if (!m.Success)
        {
            m = SeasonPackRegex().Match(text);
        }

        var before = m.Success ? text[..m.Index] : text;
        int? year = null;
        var ym = YearRegex().Match(before);
        if (ym.Success && ym.Index > 0)
        {
            year = int.Parse(ym.Groups["year"].Value, CultureInfo.InvariantCulture);
            before = before[..ym.Index];
        }

        var cleaned = CleanTitle(before);
        return (cleaned.Length > 0 ? cleaned : null, year);
    }

    private static (int? Season, List<int> Episodes) ParseEpisodes(string text)
    {
        var m = EpisodeRegex().Match(text);
        if (m.Success)
        {
            var season = int.Parse(m.Groups["season"].Value, CultureInfo.InvariantCulture);
            var episodes = m.Groups["episode"].Captures.Select(c => int.Parse(c.Value, CultureInfo.InvariantCulture)).ToList();
            if (m.Groups["rangeEnd"].Success && episodes.Count == 1)
            {
                var end = int.Parse(m.Groups["rangeEnd"].Value, CultureInfo.InvariantCulture);
                for (var e = episodes[0] + 1; e <= end && e - episodes[0] < 100; e++)
                {
                    episodes.Add(e);
                }
            }

            return (season, episodes);
        }

        var sp = SeasonPackRegex().Match(text);
        return sp.Success ? (int.Parse(sp.Groups["season"].Value, CultureInfo.InvariantCulture), []) : (null, []);
    }

    private static int ParseResolution(string text)
    {
        if (R2160().IsMatch(text)) return 2160;
        if (R1080().IsMatch(text)) return 1080;
        if (R720().IsMatch(text)) return 720;
        if (R576().IsMatch(text)) return 576;
        if (R480().IsMatch(text)) return 480;
        return 0;
    }

    private static QualitySource ParseSource(string text)
    {
        if (RemuxRegex().IsMatch(text)) return QualitySource.Remux;
        if (BlurayRegex().IsMatch(text)) return QualitySource.Bluray;
        if (WebRipRegex().IsMatch(text)) return QualitySource.WebRip;
        if (WebDlRegex().IsMatch(text)) return QualitySource.WebDl;
        if (HdtvRegex().IsMatch(text)) return QualitySource.Tv;
        if (CamRegex().IsMatch(text)) return QualitySource.Cam;
        if (TelesyncRegex().IsMatch(text)) return QualitySource.Telesync;
        if (TelecineRegex().IsMatch(text)) return QualitySource.Telecine;
        if (WorkprintRegex().IsMatch(text)) return QualitySource.Workprint;
        if (DvdRegex().IsMatch(text)) return QualitySource.Dvd;
        return QualitySource.Unknown;
    }

    private static string? ParseReleaseGroup(string title)
    {
        var m = ReleaseGroupRegex().Match(title);
        if (!m.Success)
        {
            return null;
        }

        var group = m.Groups["group"].Value;
        return InvalidGroupRegex().IsMatch(group) ? null : group;
    }

    private static string CleanTitle(string raw)
    {
        var t = raw.Trim(' ', '-', '(', '[', ')', ']', ':');
        t = MultiSpace().Replace(t, " ");
        return t.Trim();
    }

    [GeneratedRegex(@"^(?:\[[^\]]*\]\s*)+")]
    private static partial Regex LeadingTag();

    [GeneratedRegex(@"[._]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"(?<![a-z0-9])[\(\[]?(?<year>(?:19|20)\d{2})[\)\]]?(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"(?<![a-z0-9])(?:2160p|1080[pi]|720p|576p|480p|4k|uhd|bluray|blu-ray|web-?dl|webrip|web|hdtv|dvdrip|bdrip|brrip|remux|x264|x265|h ?26[45]|hevc|proper|repack|internal|multi|complete)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalTokenRegex();

    [GeneratedRegex(@"(?<![a-z0-9])S(?<season>\d{1,2})(?:\s?E(?<episode>\d{1,3}))+(?:-E?(?<rangeEnd>\d{1,3}))?(?![0-9])|(?<![a-z0-9])(?<season>\d{1,2})x(?<episode>\d{2,3})(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeRegex();

    [GeneratedRegex(@"(?<![a-z0-9])(?:S(?<season>\d{1,2})|Season\s?(?<season>\d{1,2}))(?![0-9e])", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonPackRegex();

    [GeneratedRegex(@"(?<![a-z0-9])(?:2160p|4k|uhd)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex R2160();

    [GeneratedRegex(@"(?<![a-z0-9])1080[pi](?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex R1080();

    [GeneratedRegex(@"(?<![a-z0-9])720p(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex R720();

    [GeneratedRegex(@"(?<![a-z0-9])576p(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex R576();

    [GeneratedRegex(@"(?<![a-z0-9])480p(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex R480();

    [GeneratedRegex(@"(?<![a-z0-9])(?:remux|bdremux)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex RemuxRegex();

    [GeneratedRegex(@"(?<![a-z0-9])(?:blu-?ray|bdrip|brrip|bd25|bd50|bluray\d*)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex BlurayRegex();

    [GeneratedRegex(@"(?<![a-z0-9])(?:web-?rip|webrip\d*)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex WebRipRegex();

    [GeneratedRegex(@"(?<![a-z0-9])(?:web-?dl|webdl|web|amzn|nf|dsnp|hmax|atvp|itunes)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex WebDlRegex();

    [GeneratedRegex(@"(?<![a-z0-9])(?:hdtv|pdtv|sdtv|dsr|tvrip|hdtvrip)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex HdtvRegex();

    [GeneratedRegex(@"(?<![a-z0-9])(?:cam|camrip|hdcam|cam-rip)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex CamRegex();

    [GeneratedRegex(@"(?<![a-z0-9])(?:ts|telesync|hdts|hd-ts|pdvd|tsrip)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex TelesyncRegex();

    [GeneratedRegex(@"(?<![a-z0-9])(?:tc|telecine|hdtc)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex TelecineRegex();

    [GeneratedRegex(@"(?<![a-z0-9])(?:workprint|wp)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex WorkprintRegex();

    [GeneratedRegex(@"(?<![a-z0-9])(?:dvd|dvdrip|dvd5|dvd9|dvdr|xvid|divx|ntsc|pal)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex DvdRegex();

    [GeneratedRegex(@"(?<![a-z0-9])proper(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex ProperRegex();

    [GeneratedRegex(@"(?<![a-z0-9])(?:repack|rerip)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex RepackRegex();

    [GeneratedRegex(@"-(?<group>[a-z0-9]+)(?:\[[^\]]*\])?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseGroupRegex();

    [GeneratedRegex(@"^(?:\d{3,4}p|x26[45]|h26[45]|hevc|dl|web|rip|hd|sd|\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex InvalidGroupRegex();
}
