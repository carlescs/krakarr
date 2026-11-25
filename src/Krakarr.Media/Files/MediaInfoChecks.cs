using System.Globalization;
using Krakarr.Core.Domain;
using Krakarr.Media.Parsing;

namespace Krakarr.Media.Files;

/// <summary>What the media info of a file says about its quality and whether the file is what its name claims.</summary>
public static class MediaInfoChecks
{
    /// <summary>A file shorter than this share of the runtime is a sample, a trailer or a fake.</summary>
    public const double MinimumRuntimeShare = 0.5;

    /// <summary>Runtimes shorter than this (minutes) are too rough to judge a file by.</summary>
    public const int MinimumRuntimeToCheck = 10;

    /// <summary>
    /// The quality with the resolution the video really has. The source (WEB-DL, Bluray…) still comes from the name, which
    /// is the only place it is recorded; sources without a resolution (CAM, TELESYNC…) and unknown resolutions are kept.
    /// </summary>
    public static Quality Correct(Quality quality, MediaInfoSummary? info)
    {
        if (info is null || info.Resolution == 0) return quality;
        if (quality.Source is QualitySource.Cam or QualitySource.Telesync or QualitySource.Telecine or QualitySource.Workprint) return quality;
        if (quality.Source == QualitySource.Dvd && info.Resolution <= 480) return quality;
        if (quality.Resolution == info.Resolution) return quality;
        var corrected = Qualities.Find(quality.Source, info.Resolution);
        return corrected == Qualities.Unknown ? quality : corrected;
    }

    /// <summary>Why a file is too short for its runtime (a sample, trailer or fake), or null when it looks fine or can't be told.</summary>
    public static string? TooShort(MediaInfoSummary? info, int runtimeMinutes)
    {
        if (info?.DurationSeconds is not { } seconds || seconds <= 0 || runtimeMinutes < MinimumRuntimeToCheck) return null;
        var minutes = seconds / 60.0;
        return minutes < runtimeMinutes * MinimumRuntimeShare
            ? string.Create(CultureInfo.InvariantCulture, $"File is only {Math.Max(1, (int)Math.Round(minutes))} min long, the runtime is {runtimeMinutes} min (sample or fake?)")
            : null;
    }

    /// <summary>
    /// Things worth flagging on a file: too short for the runtime, a resolution other than the name claims, or audio in
    /// none of the wanted languages. Audio language tags are often missing or wrong, so this only warns.
    /// </summary>
    public static IReadOnlyList<string> Warnings(MediaInfoSummary? info, int runtimeMinutes, string? sceneName, IReadOnlyList<string>? wantedLanguages)
    {
        if (info is null) return [];
        var warnings = new List<string>();
        if (TooShort(info, runtimeMinutes) is { } shortMessage) warnings.Add(shortMessage);
        if (Mislabelled(info, sceneName) is { } label) warnings.Add(label);
        if (UnwantedAudio(info, wantedLanguages) is { } audio) warnings.Add(audio);
        return warnings;
    }

    /// <summary>Message when the release name claims a resolution the video doesn't have ("2160p" that is really 720p).</summary>
    public static string? Mislabelled(MediaInfoSummary? info, string? sceneName)
    {
        if (info is null || info.Resolution == 0 || string.IsNullOrWhiteSpace(sceneName)) return null;
        var claimed = ReleaseParser.ParseResolution(sceneName);
        if (claimed == 576) claimed = 480;
        return claimed > 0 && claimed != info.Resolution
            ? string.Create(CultureInfo.InvariantCulture, $"Named {claimed}p but the video is {info.Width}x{info.Height} ({info.Resolution}p)")
            : null;
    }

    /// <summary>Message when the file's tagged audio languages include none of the wanted ones.</summary>
    public static string? UnwantedAudio(MediaInfoSummary? info, IReadOnlyList<string>? wanted)
    {
        if (info is null || wanted is not { Count: > 0 }) return null;
        var languages = info.AudioLanguages;
        if (languages.Count == 0) return null;
        return ReleaseLanguages.PreferenceRank(languages, wanted) is null
            ? $"Audio is {ReleaseLanguages.Names(languages)}; wanted: {ReleaseLanguages.Names(wanted)}"
            : null;
    }

    /// <summary>Adds the <c>{MediaInfo …}</c> naming tokens. Missing values expand to nothing.</summary>
    public static void AddTokens(IDictionary<string, string?> tokens, MediaInfoSummary? info)
    {
        var audio = info?.Audio.Count > 0 ? info.Audio[0] : null;
        tokens["MediaInfo VideoCodec"] = info?.VideoCodec;
        tokens["MediaInfo VideoBitDepth"] = info?.BitDepth?.ToString(CultureInfo.InvariantCulture);
        tokens["MediaInfo VideoDynamicRange"] = info?.DynamicRange;
        tokens["MediaInfo Resolution"] = info is { Resolution: > 0 } ? string.Create(CultureInfo.InvariantCulture, $"{info.Resolution}p") : null;
        tokens["MediaInfo AudioCodec"] = audio?.Codec;
        tokens["MediaInfo AudioChannels"] = info?.AudioChannels;
        tokens["MediaInfo AudioLanguages"] = info is { AudioLanguages.Count: > 0 } ? string.Join('+', info.AudioLanguages.Select(l => l.ToUpperInvariant())) : null;
        tokens["MediaInfo Simple"] = info is null ? null : string.Join(' ', new[] { info.VideoCodec, audio?.Codec }.Where(s => !string.IsNullOrEmpty(s)));
    }
}
