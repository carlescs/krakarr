using Krakarr.Core.Domain;

namespace Krakarr.Media.Parsing;

/// <summary>Works out a release's audio languages and checks them against the wanted ones.</summary>
public static class ReleaseLanguages
{
    /// <summary>
    /// Languages tagged in the name plus those the indexer reported. Untagged releases are assumed to be in the indexer's
    /// default language (English when it has none). MULTi/DUAL releases also carry the indexer's language and English,
    /// the usual original track.
    /// </summary>
    public static IReadOnlyList<string> Resolve(ParsedRelease parsed, ReleaseInfo release)
    {
        var languages = new List<string>(parsed.Languages);
        foreach (var code in release.Languages)
        {
            if (!languages.Contains(code)) languages.Add(code);
        }

        var fallback = MediaLanguages.Normalize(release.IndexerLanguage) ?? MediaLanguages.English;
        if (languages.Count == 0)
        {
            languages.Add(fallback);
        }

        if (parsed.IsMultiLanguage)
        {
            if (!languages.Contains(fallback)) languages.Add(fallback);
            if (!languages.Contains(MediaLanguages.English)) languages.Add(MediaLanguages.English);
        }

        return languages;
    }

    /// <summary>
    /// Position of the best wanted language the release has (0 = most preferred), 0 when any language is fine,
    /// or null when the release has none of the wanted languages.
    /// </summary>
    public static int? PreferenceRank(IReadOnlyList<string> releaseLanguages, IReadOnlyList<string>? wanted)
    {
        if (wanted is not { Count: > 0 }) return 0;
        for (var i = 0; i < wanted.Count; i++)
        {
            if (releaseLanguages.Contains(wanted[i], StringComparer.OrdinalIgnoreCase)) return i;
        }

        return null;
    }

    /// <summary>Rejection message for a release in none of the wanted languages.</summary>
    public static string Rejection(IReadOnlyList<string> releaseLanguages, IReadOnlyList<string> wanted) =>
        $"Language not wanted ({Names(releaseLanguages)}; wanted: {Names(wanted)})";

    public static string Names(IEnumerable<string> codes) => string.Join(", ", codes.Select(MediaLanguages.Name));
}
