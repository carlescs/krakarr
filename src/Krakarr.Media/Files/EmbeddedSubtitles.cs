namespace Krakarr.Media.Files;

/// <summary>A subtitle track stored inside a video container. Language is the raw tag (ISO 639-2 or BCP 47), or null.</summary>
public sealed record EmbeddedSubtitleTrack(string? Language, string? Name, string? Codec, bool Forced, bool HearingImpaired);

/// <summary>
/// Lists the subtitle tracks of Matroska/WebM and MP4/MOV files by reading their track headers only (see
/// <see cref="MediaInfoReader"/>). Unknown or damaged files give an empty list.
/// </summary>
public static class EmbeddedSubtitles
{
    public static IReadOnlyList<EmbeddedSubtitleTrack> Read(string videoPath) => MediaInfoReader.ReadHeaders(videoPath).Subtitles;

    public static IReadOnlyList<EmbeddedSubtitleTrack> Read(Stream stream) => MediaInfoReader.Read(stream).Subtitles;
}
