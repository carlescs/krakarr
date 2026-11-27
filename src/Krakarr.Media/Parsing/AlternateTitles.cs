using Krakarr.Core.Domain;

namespace Krakarr.Media.Parsing;

/// <summary>Picks, normalises and stores the alternate titles of a movie or series (see <see cref="AlternateTitle"/>).</summary>
public static class AlternateTitles
{
    /// <summary>Most titles kept per source and item, so a title with hundreds of AKAs stays small.</summary>
    public const int MaxPerSource = 30;

    /// <summary>Most user titles per item.</summary>
    public const int MaxUserTitles = 20;

    /// <summary>Spanish-speaking countries of Latin America (and the US, whose Spanish dubs are Latin American).</summary>
    private static readonly HashSet<string> LatinAmerica = new(StringComparer.OrdinalIgnoreCase)
    {
        "MX", "AR", "CO", "CL", "PE", "VE", "EC", "GT", "CU", "BO", "DO", "HN", "PY", "SV", "NI", "CR", "PA", "UY", "PR", "US",
    };

    /// <summary>Main language of a country, for TMDB alternative titles (which only carry a country).</summary>
    private static readonly Dictionary<string, string> CountryLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = "en", ["GB"] = "en", ["AU"] = "en", ["CA"] = "en", ["IE"] = "en", ["NZ"] = "en",
        ["ES"] = "es", ["FR"] = "fr", ["BE"] = "fr", ["DE"] = "de", ["AT"] = "de", ["CH"] = "de", ["IT"] = "it",
        ["PT"] = "pt", ["BR"] = "pt-br", ["NL"] = "nl", ["SE"] = "sv", ["DK"] = "da", ["NO"] = "no", ["FI"] = "fi",
        ["IS"] = "is", ["PL"] = "pl", ["RU"] = "ru", ["UA"] = "uk", ["CZ"] = "cs", ["SK"] = "sk", ["HU"] = "hu",
        ["RO"] = "ro", ["BG"] = "bg", ["GR"] = "el", ["TR"] = "tr", ["IL"] = "he", ["IR"] = "fa", ["IN"] = "hi",
        ["JP"] = "ja", ["KR"] = "ko", ["CN"] = "zh", ["TW"] = "zh", ["HK"] = "zh", ["VN"] = "vi", ["TH"] = "th",
        ["ID"] = "id", ["HR"] = "hr", ["RS"] = "sr", ["SI"] = "sl", ["EE"] = "et", ["LV"] = "lv", ["LT"] = "lt",
    };

    /// <summary>
    /// Krakarr language code for a TMDB language ("es", ISO 639-1) and/or country ("MX", ISO 3166-1): Spanish in Latin
    /// America is <c>es-419</c>, Portuguese in Brazil <c>pt-br</c>. Null when neither is known.
    /// </summary>
    public static string? LanguageFor(string? language, string? country)
    {
        var lang = language?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(lang))
        {
            if (string.IsNullOrWhiteSpace(country)) return null;
            if (LatinAmerica.Contains(country) && !country.Equals("US", StringComparison.OrdinalIgnoreCase)) return "es-419";
            return CountryLanguages.GetValueOrDefault(country.Trim());
        }

        if (lang == "es" && country is not null && LatinAmerica.Contains(country)) return "es-419";
        if (lang == "pt" && "BR".Equals(country, StringComparison.OrdinalIgnoreCase)) return "pt-br";
        return MediaLanguages.IsKnown(lang) ? lang : null;
    }

    /// <summary>
    /// Keeps the titles worth matching: not blank, not too long, distinct once normalised and different from the main
    /// titles. With <paramref name="languages"/>, only titles of unknown language or in one of those languages are kept.
    /// </summary>
    public static List<AlternateTitleInfo> Select(
        IEnumerable<AlternateTitleInfo> candidates, IEnumerable<string?> mainTitles, Func<string, string> normalize,
        IReadOnlySet<string>? languages = null, int max = MaxPerSource)
    {
        var seen = mainTitles.OfType<string>().Select(normalize).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
        var kept = new List<AlternateTitleInfo>();
        foreach (var candidate in candidates)
        {
            if (kept.Count >= max) break;
            var title = candidate.Title?.Trim();
            if (string.IsNullOrEmpty(title) || title.Length > AlternateTitle.MaxTitleLength) continue;
            if (languages is not null && candidate.Language is { } lang && !languages.Contains(lang)) continue;
            var normalized = normalize(title);
            if (normalized.Length == 0 || !seen.Add(normalized)) continue;
            kept.Add(candidate with { Title = title });
        }

        return kept;
    }

    /// <summary>
    /// Makes the titles of <paramref name="source"/> in <paramref name="current"/> (an item's loaded collection) match
    /// <paramref name="desired"/>: new ones are added to the collection, changed languages are updated, and the ones no
    /// longer wanted are removed from it and returned so the caller deletes them.
    /// </summary>
    public static IReadOnlyList<AlternateTitle> Sync(
        List<AlternateTitle> current, AlternateTitleSource source, IEnumerable<AlternateTitleInfo> desired, Func<string, string> normalize)
    {
        var wanted = new Dictionary<string, AlternateTitleInfo>(StringComparer.Ordinal);
        foreach (var d in desired)
        {
            var normalized = normalize(d.Title);
            if (normalized.Length > 0) wanted.TryAdd(normalized, d);
        }

        var removed = new List<AlternateTitle>();
        foreach (var existing in current.Where(t => t.Source == source).ToList())
        {
            if (wanted.Remove(existing.NormalizedTitle, out var keep))
            {
                existing.Title = keep.Title;
                existing.Language = keep.Language;
                continue;
            }

            current.Remove(existing);
            removed.Add(existing);
        }

        foreach (var (normalized, info) in wanted)
        {
            current.Add(new AlternateTitle { Title = info.Title, NormalizedTitle = normalized, Language = info.Language, Source = source });
        }

        return removed;
    }

    /// <summary>
    /// Replaces the user's titles in <paramref name="current"/> (an item's loaded collection) with <paramref name="requested"/>.
    /// Languages are normalised (unknown ones become null); blank titles, duplicates and the main titles are dropped.
    /// </summary>
    /// <returns>The titles to delete, or an error message when the request is invalid (nothing is changed then).</returns>
    public static (IReadOnlyList<AlternateTitle> Removed, string? Error) SetUserTitles(
        List<AlternateTitle> current, IReadOnlyList<AlternateTitleInfo>? requested, IEnumerable<string?> mainTitles, Func<string, string> normalize)
    {
        var list = requested ?? [];
        if (list.Count > MaxUserTitles) return ([], $"At most {MaxUserTitles} titles");
        if (list.Any(t => t.Title?.Trim().Length > AlternateTitle.MaxTitleLength))
        {
            return ([], $"Titles must be at most {AlternateTitle.MaxTitleLength} characters");
        }

        var cleaned = list.Select(t => new AlternateTitleInfo(t.Title ?? string.Empty, MediaLanguages.Normalize(t.Language)));
        var titles = Select(cleaned, mainTitles, normalize, max: MaxUserTitles);
        return (Sync(current, AlternateTitleSource.User, titles, normalize), null);
    }

    /// <summary>The titles of an item for the API: the user's first, then scene names and metadata titles.</summary>
    public static IEnumerable<AlternateTitleDto> ToDtos(IEnumerable<AlternateTitle> titles) =>
        titles
            .OrderBy(t => t.Source switch { AlternateTitleSource.User => 0, AlternateTitleSource.Scene => 1, _ => 2 })
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(t => new AlternateTitleDto(t.Id, t.Title, t.Language, t.Source));

    /// <summary>
    /// Alternate titles to text-search on an indexer whose untagged releases are in <paramref name="indexerLanguage"/>
    /// (null = English): titles in that language, and titles of unknown language. At most <paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<string> ForIndexer(IEnumerable<AlternateTitleInfo> titles, string? indexerLanguage, int max)
    {
        var lang = indexerLanguage ?? MediaLanguages.English;
        return titles
            .Where(t => t.Language is null || string.Equals(t.Language, lang, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Language is null) // the indexer's own language first
            .Select(t => t.Title)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }
}
