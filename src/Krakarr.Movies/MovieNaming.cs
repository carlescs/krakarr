using Krakarr.Core.Domain;
using Krakarr.Media.Files;

namespace Krakarr.Movies;

public static class MovieNaming
{
    public static Dictionary<string, string?> Tokens(Movie movie, Quality? quality = null, string? releaseGroup = null) => new()
    {
        ["Movie Title"] = movie.Title,
        ["Movie CleanTitle"] = new string(movie.Title.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()),
        ["Original Title"] = movie.OriginalTitle ?? movie.Title,
        ["Release Year"] = movie.Year > 0 ? movie.Year.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
        ["Quality"] = quality?.Name,
        ["Release Group"] = releaseGroup,
        ["TmdbId"] = movie.TmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["ImdbId"] = movie.ImdbId,
    };

    public static string BuildFolderName(Movie movie, string format) => FileNameBuilder.Build(format, Tokens(movie));

    public static string BuildFileName(Movie movie, string format, Quality quality, string? releaseGroup, string extension, MediaInfoSummary? mediaInfo = null)
    {
        var tokens = Tokens(movie, quality, releaseGroup);
        MediaInfoChecks.AddTokens(tokens, mediaInfo);
        return FileNameBuilder.Build(format, tokens) + extension.ToLowerInvariant();
    }
}
