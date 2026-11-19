namespace Krakarr.Subtitles;

public sealed record SubtitleLanguage(string Code, string Name, IReadOnlyList<string> Aliases);

/// <summary>Languages Krakarr can search subtitles for, keyed by ISO 639-1 (plus regional variants).</summary>
public static class Languages
{
    public static IReadOnlyList<SubtitleLanguage> All { get; } =
    [
        new("en", "English", ["eng", "english"]),
        new("es", "Spanish", ["spa", "spanish", "espanol", "español", "castellano"]),
        new("ca", "Catalan", ["cat", "catalan", "català"]),
        new("fr", "French", ["fra", "fre", "french", "francais", "français"]),
        new("de", "German", ["deu", "ger", "german", "deutsch"]),
        new("it", "Italian", ["ita", "italian", "italiano"]),
        new("pt", "Portuguese", ["por", "portuguese", "pt-pt"]),
        new("pt-br", "Portuguese (Brazil)", ["pob", "ptbr", "pb", "brazilian"]),
        new("nl", "Dutch", ["nld", "dut", "dutch", "nederlands"]),
        new("sv", "Swedish", ["swe", "swedish", "svenska"]),
        new("da", "Danish", ["dan", "danish", "dansk"]),
        new("no", "Norwegian", ["nor", "nob", "nb", "norwegian", "norsk"]),
        new("fi", "Finnish", ["fin", "finnish", "suomi"]),
        new("pl", "Polish", ["pol", "polish", "polski"]),
        new("ru", "Russian", ["rus", "russian"]),
        new("uk", "Ukrainian", ["ukr", "ukrainian"]),
        new("cs", "Czech", ["ces", "cze", "czech"]),
        new("sk", "Slovak", ["slk", "slo", "slovak"]),
        new("hu", "Hungarian", ["hun", "hungarian", "magyar"]),
        new("ro", "Romanian", ["ron", "rum", "romanian"]),
        new("bg", "Bulgarian", ["bul", "bulgarian"]),
        new("el", "Greek", ["ell", "gre", "greek"]),
        new("tr", "Turkish", ["tur", "turkish"]),
        new("he", "Hebrew", ["heb", "hebrew"]),
        new("ar", "Arabic", ["ara", "arabic"]),
        new("fa", "Persian", ["fas", "per", "persian", "farsi"]),
        new("hi", "Hindi", ["hin", "hindi"]),
        new("ja", "Japanese", ["jpn", "japanese"]),
        new("ko", "Korean", ["kor", "korean"]),
        new("zh-cn", "Chinese (Simplified)", ["chi", "zho", "chs", "zh", "chinese", "zh-hans"]),
        new("zh-tw", "Chinese (Traditional)", ["zht", "cht", "zh-hant"]),
        new("vi", "Vietnamese", ["vie", "vietnamese"]),
        new("th", "Thai", ["tha", "thai"]),
        new("id", "Indonesian", ["ind", "indonesian"]),
        new("ms", "Malay", ["msa", "may", "malay"]),
        new("hr", "Croatian", ["hrv", "croatian"]),
        new("sr", "Serbian", ["srp", "serbian"]),
        new("sl", "Slovenian", ["slv", "slovenian"]),
        new("et", "Estonian", ["est", "estonian"]),
        new("lv", "Latvian", ["lav", "latvian"]),
        new("lt", "Lithuanian", ["lit", "lithuanian"]),
        new("eu", "Basque", ["eus", "baq", "basque", "euskara"]),
        new("gl", "Galician", ["glg", "galician", "galego"]),
    ];

    private static readonly Dictionary<string, string> Lookup = BuildLookup();

    private static Dictionary<string, string> BuildLookup()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in All)
        {
            map[lang.Code] = lang.Code;
            foreach (var alias in lang.Aliases) map.TryAdd(alias, lang.Code);
        }

        return map;
    }

    /// <summary>Normalizes a language code, alias or name ("eng", "English", "pt-BR") to Krakarr's code, or null.</summary>
    public static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Lookup.GetValueOrDefault(value.Trim().Replace('_', '-'));

    public static string Name(string code) => All.FirstOrDefault(l => l.Code == code)?.Name ?? code;

    private static readonly string[] HearingImpairedTokens = ["hi", "sdh", "cc"];
    private static readonly string[] ForcedTokens = ["forced", "foreign"];

    /// <summary>
    /// Reads language and flags from a subtitle file name that follows a video's base name,
    /// e.g. "Movie (2020).en.hi.srt" or "Movie.English.forced.srt". Returns null when no language is recognised.
    /// </summary>
    public static (string Language, bool HearingImpaired, bool Forced)? FromSidecarName(string subtitleFileName, string videoBaseName)
    {
        var name = Path.GetFileNameWithoutExtension(subtitleFileName);
        if (!name.StartsWith(videoBaseName, StringComparison.OrdinalIgnoreCase)) return null;
        var tokens = name[videoBaseName.Length..].Split(['.', ' ', '_'], StringSplitOptions.RemoveEmptyEntries);

        string? language = null;
        bool hi = false, forced = false;
        foreach (var token in tokens)
        {
            if (HearingImpairedTokens.Contains(token, StringComparer.OrdinalIgnoreCase)) hi = true;
            else if (ForcedTokens.Contains(token, StringComparer.OrdinalIgnoreCase)) forced = true;
            else language ??= Normalize(token);
        }

        return language is null ? null : (language, hi, forced);
    }
}
