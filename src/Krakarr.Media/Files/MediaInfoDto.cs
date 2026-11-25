using Krakarr.Core.Domain;

namespace Krakarr.Media.Files;

/// <summary>An audio track for display. Channels is the layout ("5.1"), LanguageName the readable language.</summary>
public sealed record AudioTrackDto(string? Codec, string? Channels, string? Language, string? LanguageName);

/// <summary>Media info of a library file for the detail pages, with the warnings worth showing next to it.</summary>
public sealed record MediaInfoDto(
    int? DurationSeconds,
    string? VideoCodec,
    int Width,
    int Height,
    string? Resolution,
    int? BitDepth,
    string? DynamicRange,
    IReadOnlyList<AudioTrackDto> Audio,
    IReadOnlyList<string> Warnings)
{
    /// <summary>The DTO of a stored summary (<see cref="MediaInfoSummary"/>), or null when there is none.</summary>
    public static MediaInfoDto? From(string? stored, int runtimeMinutes, string? sceneName, IReadOnlyList<string>? wantedLanguages)
    {
        if (MediaInfoSummary.Parse(stored) is not { } info) return null;
        return new MediaInfoDto(
            info.DurationSeconds,
            info.VideoCodec,
            info.Width,
            info.Height,
            info.Resolution > 0 ? $"{info.Resolution}p" : null,
            info.BitDepth,
            info.DynamicRange,
            info.Audio.Select(a => new AudioTrackDto(a.Codec, MediaInfoSummary.FormatChannels(a.Channels), a.Language,
                a.Language is null ? null : MediaLanguages.IsKnown(a.Language) ? MediaLanguages.Name(a.Language) : a.Language)).ToList(),
            MediaInfoChecks.Warnings(info, runtimeMinutes, sceneName, wantedLanguages));
    }
}
