using Krakarr.Media.Files;

namespace Krakarr.Subtitles;

public sealed record EmbeddedSubtitle(string Language, bool HearingImpaired, bool Forced);

/// <summary>
/// Converts the subtitle tracks inside a video to and from the compact form stored on movie and episode files:
/// comma-separated "lang[+hi][+forced]" tokens with Krakarr language codes ("en,es+forced").
/// </summary>
public static class EmbeddedSubtitleList
{
    public static IReadOnlyList<EmbeddedSubtitle> FromTracks(IEnumerable<EmbeddedSubtitleTrack> tracks) =>
        tracks.Select(t => NormalizeLanguage(t.Language) is { } language ? new EmbeddedSubtitle(language, t.HearingImpaired, t.Forced) : null)
            .OfType<EmbeddedSubtitle>()
            .Distinct()
            .ToList();

    /// <summary>"eng", "pt-BR" or "es-419" to a Krakarr code; "und" and unknown languages give null.</summary>
    public static string? NormalizeLanguage(string? tag)
    {
        if (Languages.Normalize(tag) is { } code) return code;
        var dash = tag?.IndexOf('-') ?? -1;
        return dash > 0 ? Languages.Normalize(tag![..dash]) : null;
    }

    public static string Format(IEnumerable<EmbeddedSubtitle> subtitles)
    {
        var value = string.Join(',', subtitles.Select(s => s.Language + (s.HearingImpaired ? "+hi" : string.Empty) + (s.Forced ? "+forced" : string.Empty)));
        // The column holds 512 characters; dozens of tracks is already unusual, so drop the tail rather than fail.
        return value.Length <= 512 ? value : value[..value[..512].LastIndexOf(',')];
    }

    public static IReadOnlyList<EmbeddedSubtitle> Parse(string? value)
    {
        if (string.IsNullOrEmpty(value)) return [];
        var result = new List<EmbeddedSubtitle>();
        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = token.Split('+');
            result.Add(new EmbeddedSubtitle(parts[0], parts.Contains("hi"), parts.Contains("forced")));
        }

        return result;
    }

    /// <summary>Languages with a non-forced embedded subtitle.</summary>
    public static IEnumerable<string> FullLanguages(string? value) => Parse(value).Where(s => !s.Forced).Select(s => s.Language);

    /// <summary>Whether an embedded track has the language (ignoring case and the hearing-impaired flag) and the same forced flag.</summary>
    public static bool Covers(string? value, string language, bool forced) =>
        Parse(value).Any(s => s.Forced == forced && string.Equals(s.Language, language, StringComparison.OrdinalIgnoreCase));
}
