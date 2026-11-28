using System.Text.RegularExpressions;

namespace Krakarr.Media.Parsing;

/// <summary>
/// Technical tags of a release name beyond quality: HDR, codecs, audio, editions, streaming services and flags.
/// The values here are what <see cref="ParsedRelease"/> holds and what custom format conditions compare against.
/// </summary>
public static partial class ReleaseTags
{
    public const string Hdr10 = "HDR10";
    public const string Hdr10Plus = "HDR10+";
    public const string DolbyVision = "DV";
    public const string Hlg = "HLG";

    /// <summary>Dynamic range values, plus "DV-only" (Dolby Vision without an HDR fallback) for conditions.</summary>
    public static IReadOnlyList<string> Hdr { get; } = [Hdr10, Hdr10Plus, DolbyVision, Hlg];

    public static IReadOnlyList<string> Codecs { get; } = ["x264", "x265", "av1", "xvid"];

    /// <summary>Audio formats, from the richest to the most basic.</summary>
    public static IReadOnlyList<string> Audio { get; } =
        ["Atmos", "TrueHD", "DTS:X", "DTS-HD MA", "DTS-HD HRA", "DTS-ES", "DTS", "DD+", "DD", "FLAC", "PCM", "AAC", "Opus", "MP3"];

    public static IReadOnlyList<string> Editions { get; } =
        ["Director's Cut", "Extended", "IMAX", "Theatrical", "Remastered", "Criterion", "Uncut", "Unrated", "Special Edition"];

    /// <summary>Streaming service codes as releases write them, with their names.</summary>
    public static IReadOnlyDictionary<string, string> StreamingServices { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["AMZN"] = "Amazon Prime Video",
        ["NF"] = "Netflix",
        ["DSNP"] = "Disney+",
        ["ATVP"] = "Apple TV+",
        ["HMAX"] = "HBO Max",
        ["MAX"] = "Max",
        ["HULU"] = "Hulu",
        ["PCOK"] = "Peacock",
        ["PMTP"] = "Paramount+",
        ["STAN"] = "Stan",
        ["CRAV"] = "Crave",
        ["iTunes"] = "iTunes",
        ["SHO"] = "Showtime",
        ["ROKU"] = "Roku",
        ["BCORE"] = "Bravia Core",
        ["CR"] = "Crunchyroll",
    };

    public const string Flag3D = "3D";
    public const string FlagHardcodedSubs = "Hardcoded subs";
    public const string FlagUpscaled = "Upscaled";

    public static IReadOnlyList<string> Flags { get; } = [Flag3D, FlagHardcodedSubs, FlagUpscaled];

    /// <summary>Tags found in the technical part of a (separator-normalized) release name, after the title.</summary>
    internal readonly record struct Found(
        IReadOnlyList<string> Hdr, IReadOnlyList<string> Audio, string? Channels, IReadOnlyList<string> Editions,
        string? StreamingService, bool Is3D, bool HardcodedSubs, bool Upscaled);

    internal static Found Detect(string tail)
    {
        List<string>? hdr = null;
        if (DolbyVisionRegex().IsMatch(tail)) (hdr ??= []).Add(DolbyVision);
        if (Hdr10PlusRegex().IsMatch(tail)) (hdr ??= []).Add(Hdr10Plus);
        // A bare "HDR" tag is HDR10, the base layer every HDR release carries.
        if (Hdr10Regex().IsMatch(tail)) (hdr ??= []).Add(Hdr10);
        if (HlgRegex().IsMatch(tail)) (hdr ??= []).Add(Hlg);

        List<string>? audio = null;
        void AddAudio(string a) { if (!(audio ??= []).Contains(a)) audio.Add(a); }
        if (AtmosRegex().IsMatch(tail)) AddAudio("Atmos");
        if (TrueHdRegex().IsMatch(tail)) AddAudio("TrueHD");
        if (DtsXRegex().IsMatch(tail)) AddAudio("DTS:X");
        if (DtsHdMaRegex().IsMatch(tail)) AddAudio("DTS-HD MA");
        if (DtsHdHraRegex().IsMatch(tail)) AddAudio("DTS-HD HRA");
        if (DtsEsRegex().IsMatch(tail)) AddAudio("DTS-ES");
        if (DtsRegex().IsMatch(tail)) AddAudio("DTS");
        if (DdPlusRegex().IsMatch(tail)) AddAudio("DD+");
        if (DdRegex().IsMatch(tail)) AddAudio("DD");
        if (FlacRegex().IsMatch(tail)) AddAudio("FLAC");
        if (PcmRegex().IsMatch(tail)) AddAudio("PCM");
        if (AacRegex().IsMatch(tail)) AddAudio("AAC");
        if (OpusRegex().IsMatch(tail)) AddAudio("Opus");
        if (Mp3Regex().IsMatch(tail)) AddAudio("MP3");

        var channels = ChannelsRegex().Match(tail) is { Success: true } ch ? $"{ch.Groups["main"].Value}.{ch.Groups["lfe"].Value}" : null;

        List<string>? editions = null;
        foreach (var (name, regex) in EditionRegexes)
        {
            if (regex.IsMatch(tail)) (editions ??= []).Add(name);
        }

        string? service = null;
        if (StreamingServiceRegex().Match(tail) is { Success: true } s)
        {
            service = StreamingServices.Keys.First(k => k.Equals(s.Groups["code"].Value, StringComparison.OrdinalIgnoreCase));
        }

        return new Found(
            hdr ?? (IReadOnlyList<string>)[], audio ?? (IReadOnlyList<string>)[], channels, editions ?? (IReadOnlyList<string>)[], service,
            ThreeDRegex().IsMatch(tail), HardcodedSubsRegex().IsMatch(tail), UpscaledRegex().IsMatch(tail));
    }

    private static readonly (string Name, Regex Regex)[] EditionRegexes =
    [
        ("Director's Cut", DirectorsCutRegex()),
        ("Extended", ExtendedRegex()),
        ("IMAX", ImaxRegex()),
        ("Theatrical", TheatricalRegex()),
        ("Remastered", RemasteredRegex()),
        ("Criterion", CriterionRegex()),
        ("Uncut", UncutRegex()),
        ("Unrated", UnratedRegex()),
        ("Special Edition", SpecialEditionRegex()),
    ];

    private const string B = "(?<![a-z0-9])";
    private const string E = "(?![a-z0-9])";

    [GeneratedRegex(B + @"(?:dv|dovi|dolby ?vision)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex DolbyVisionRegex();

    [GeneratedRegex(B + @"hdr ?10(?:\+|plus|p(?![a-z0-9]))", RegexOptions.IgnoreCase)]
    private static partial Regex Hdr10PlusRegex();

    [GeneratedRegex(B + @"hdr(?: ?10)?(?![a-z0-9+]|plus)", RegexOptions.IgnoreCase)]
    private static partial Regex Hdr10Regex();

    [GeneratedRegex(B + "hlg" + E, RegexOptions.IgnoreCase)]
    private static partial Regex HlgRegex();

    [GeneratedRegex(B + "atmos" + E, RegexOptions.IgnoreCase)]
    private static partial Regex AtmosRegex();

    [GeneratedRegex(B + @"true ?hd(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex TrueHdRegex();

    [GeneratedRegex(B + @"dts[ :-]?x(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex DtsXRegex();

    [GeneratedRegex(B + @"dts[ -]?hd[ -]?ma(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex DtsHdMaRegex();

    [GeneratedRegex(B + @"dts[ -]?hd[ -]?hra(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex DtsHdHraRegex();

    [GeneratedRegex(B + @"dts[ -]?es(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex DtsEsRegex();

    // Plain DTS, not the start of one of the richer DTS tags above.
    [GeneratedRegex(B + @"dts(?![a-z]|[ :-]?x(?![a-z0-9])|[ -]?hd|[ -]?es(?![a-z]))", RegexOptions.IgnoreCase)]
    private static partial Regex DtsRegex();

    [GeneratedRegex(B + @"(?:dd\+|ddp(?=[0-9]|[^a-z0-9]|$)|e-?ac-?3(?![a-z0-9]))", RegexOptions.IgnoreCase)]
    private static partial Regex DdPlusRegex();

    // "DD5.1" / "DD 2.0" / "AC3", not "DD+" or "DDP".
    [GeneratedRegex(B + @"(?:dd(?= ?[1-9] ?[ .]?[01](?![0-9]))|(?<!e-?)ac-?3(?![a-z0-9]))", RegexOptions.IgnoreCase)]
    private static partial Regex DdRegex();

    [GeneratedRegex(B + @"flac(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex FlacRegex();

    [GeneratedRegex(B + @"l?pcm(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex PcmRegex();

    [GeneratedRegex(B + @"aac(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex AacRegex();

    [GeneratedRegex(B + @"opus(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex OpusRegex();

    [GeneratedRegex(B + @"mp3(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Mp3Regex();

    // Channels follow an audio tag: "DDP5 1", "TrueHD 7 1", "AAC2 0", "DTS-HD MA 5 1", "Atmos 7 1" (dots are already spaces).
    [GeneratedRegex(B + @"(?:dd\+?|ddp|e?-?ac-?3|aac|true ?hd|dts(?:[ -]?hd)?(?:[ -]?(?:ma|hra))?|dts[ :-]?x|flac|opus|l?pcm|atmos|mp3)[ -]?(?<main>[1-9]) ?[ .]?(?<lfe>[01])(?:ch)?(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelsRegex();

    [GeneratedRegex(B + @"directors?'?s? ?cut" + E, RegexOptions.IgnoreCase)]
    private static partial Regex DirectorsCutRegex();

    [GeneratedRegex(B + @"extended(?: (?:cut|edition|version))?" + E, RegexOptions.IgnoreCase)]
    private static partial Regex ExtendedRegex();

    [GeneratedRegex(B + "imax" + E, RegexOptions.IgnoreCase)]
    private static partial Regex ImaxRegex();

    [GeneratedRegex(B + @"theatrical(?: (?:cut|edition|version))?" + E, RegexOptions.IgnoreCase)]
    private static partial Regex TheatricalRegex();

    [GeneratedRegex(B + @"(?:4k ?)?remaster(?:ed)?" + E, RegexOptions.IgnoreCase)]
    private static partial Regex RemasteredRegex();

    [GeneratedRegex(B + "criterion" + E, RegexOptions.IgnoreCase)]
    private static partial Regex CriterionRegex();

    [GeneratedRegex(B + "uncut" + E, RegexOptions.IgnoreCase)]
    private static partial Regex UncutRegex();

    [GeneratedRegex(B + "unrated" + E, RegexOptions.IgnoreCase)]
    private static partial Regex UnratedRegex();

    [GeneratedRegex(B + "special edition" + E, RegexOptions.IgnoreCase)]
    private static partial Regex SpecialEditionRegex();

    // Case-sensitive: lower-case "nf", "cr" or "max" are words, not service tags. "MAX" only counts right before "WEB".
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<code>AMZN|NF|DSNP|ATVP|HMAX|MAX(?= WEB)|HULU|Hulu|PCOK|PMTP|STAN|CRAV|iTunes|ITUNES|SHO|ROKU|BCORE|CR(?= WEB))(?![A-Za-z0-9])")]
    private static partial Regex StreamingServiceRegex();

    [GeneratedRegex(B + @"(?:3d|h-?sbs|h-?ou|half[ -]?(?:sbs|ou)|sbs)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex ThreeDRegex();

    [GeneratedRegex(B + @"(?:hc|hardsub(?:bed|s)?|hard[ -]?coded?(?: subs?)?|korsub)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex HardcodedSubsRegex();

    [GeneratedRegex(B + @"(?:upscaled?|upscaling|ai[ -]?upscale[ds]?|regraded? upscale)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex UpscaledRegex();
}
