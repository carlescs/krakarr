namespace Krakarr.Core.Domain;

/// <param name="Code">ISO 639-1 code, or a regional variant ("pt-br", "es-419").</param>
/// <param name="Tokens">Words release names use to tag this audio language ("FRENCH", "VFF", "CASTELLANO").</param>
public sealed record MediaLanguage(string Code, string Name, IReadOnlyList<string> Tokens);

/// <summary>Audio languages Krakarr can recognise in release names and filter releases by.</summary>
public static class MediaLanguages
{
    public const string English = "en";

    public static IReadOnlyList<MediaLanguage> All { get; } =
    [
        new("en", "English", ["english", "eng"]),
        new("es", "Spanish", ["spanish", "castellano", "espanol", "español", "esp", "spa"]),
        new("es-419", "Spanish (Latin America)", ["latino", "lat"]),
        new("ca", "Catalan", ["catalan", "català", "cat"]),
        new("fr", "French", ["french", "truefrench", "vff", "vfq", "vfi", "vf", "vf2", "fra", "fre"]),
        new("de", "German", ["german", "deutsch", "ger"]),
        new("it", "Italian", ["italian", "italiano", "ita"]),
        new("pt", "Portuguese", ["portuguese", "por"]),
        new("pt-br", "Portuguese (Brazil)", ["brazilian", "ptbr", "pt-br", "pt br", "dublado"]),
        new("nl", "Dutch", ["dutch", "flemish", "nld"]),
        new("sv", "Swedish", ["swedish", "swe"]),
        new("da", "Danish", ["danish"]),
        new("no", "Norwegian", ["norwegian", "nor"]),
        new("fi", "Finnish", ["finnish"]),
        new("is", "Icelandic", ["icelandic"]),
        new("pl", "Polish", ["polish", "pldub", "pol"]),
        new("ru", "Russian", ["russian", "rus"]),
        new("uk", "Ukrainian", ["ukrainian", "ukr"]),
        new("cs", "Czech", ["czech", "cze"]),
        new("sk", "Slovak", ["slovak"]),
        new("hu", "Hungarian", ["hungarian", "hun"]),
        new("ro", "Romanian", ["romanian"]),
        new("bg", "Bulgarian", ["bulgarian"]),
        new("el", "Greek", ["greek"]),
        new("tr", "Turkish", ["turkish", "tur"]),
        new("he", "Hebrew", ["hebrew", "heb"]),
        new("ar", "Arabic", ["arabic"]),
        new("fa", "Persian", ["persian", "farsi"]),
        new("hi", "Hindi", ["hindi"]),
        new("ta", "Tamil", ["tamil"]),
        new("te", "Telugu", ["telugu"]),
        new("ja", "Japanese", ["japanese", "jpn"]),
        new("ko", "Korean", ["korean", "kor"]),
        new("zh", "Chinese", ["chinese", "mandarin", "cantonese", "chi"]),
        new("vi", "Vietnamese", ["vietnamese"]),
        new("th", "Thai", ["thai"]),
        new("id", "Indonesian", ["indonesian"]),
        new("hr", "Croatian", ["croatian"]),
        new("sr", "Serbian", ["serbian"]),
        new("sl", "Slovenian", ["slovenian"]),
        new("et", "Estonian", ["estonian"]),
        new("lv", "Latvian", ["latvian"]),
        new("lt", "Lithuanian", ["lithuanian"]),
        new("eu", "Basque", ["basque", "euskara"]),
        new("gl", "Galician", ["galician", "galego"]),
    ];

    private static readonly Dictionary<string, MediaLanguage> ByCode = All.ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> ByName = BuildNameLookup();

    private static Dictionary<string, string> BuildNameLookup()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in All)
        {
            map.TryAdd(lang.Code, lang.Code);
            map.TryAdd(lang.Name, lang.Code);
            foreach (var token in lang.Tokens) map.TryAdd(token, lang.Code);
        }

        return map;
    }

    public static bool IsKnown(string? code) => code is not null && ByCode.ContainsKey(code);

    /// <summary>Maps a code, name or release token ("ENG", "French", "pt-BR") to Krakarr's code, or null.</summary>
    public static string? Normalize(string? value)
    {
        var v = value?.Trim().Replace('_', '-');
        return string.IsNullOrEmpty(v) ? null : ByName.GetValueOrDefault(v);
    }

    public static string Name(string code) => ByCode.TryGetValue(code, out var lang) ? lang.Name : code;

    /// <summary>Normalizes a list of language codes: unknown and duplicate entries are dropped, order is kept.</summary>
    public static List<string> Clean(IEnumerable<string>? codes) =>
        (codes ?? []).Select(Normalize).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
