using Krakarr.Core.Domain;

namespace Krakarr.Media.Files;

/// <summary>Fills in what a library file's headers say, reading each file at most once.</summary>
public static class MediaFileProbe
{
    /// <summary>
    /// Reads the file when its media info or subtitle tracks haven't been read yet. Fresh media info is stored and
    /// corrects the file's quality to the resolution the video really has. Returns the subtitle tracks read, or null
    /// when nothing needed reading (or the file is missing); the caller stores those.
    /// </summary>
    public static IReadOnlyList<EmbeddedSubtitleTrack>? Probe(string videoPath, ILibraryFile file)
    {
        if ((file.MediaInfo is not null && file.EmbeddedSubtitles is not null) || !File.Exists(videoPath)) return null;
        if (file.MediaInfo is not null) return MediaInfoReader.ReadHeaders(videoPath).Subtitles;

        var info = MediaInfoReader.Read(videoPath);
        Apply(file, MediaInfoSummary.From(info));
        return info.Subtitles;
    }

    /// <summary>Stores a summary on the file and corrects its quality by it.</summary>
    public static void Apply(ILibraryFile file, MediaInfoSummary? summary)
    {
        file.MediaInfo = MediaInfoSummary.Serialize(summary);
        file.QualityId = MediaInfoChecks.Correct(Qualities.FindById(file.QualityId), summary).Id;
    }
}
