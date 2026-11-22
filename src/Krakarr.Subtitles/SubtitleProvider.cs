using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;

namespace Krakarr.Subtitles;

public class SubtitleProviderException(string message) : Exception(message);

/// <summary>The provider asked us to back off (rate limit or download quota); nothing was sent.</summary>
public sealed class SubtitleRateLimitException(string message, DateTimeOffset until) : SubtitleProviderException(message)
{
    public DateTimeOffset Until { get; } = until;

    public static SubtitleRateLimitException RateLimited(DateTimeOffset until) =>
        new($"OpenSubtitles rate limit reached; paused until {until.ToLocalTime():HH:mm:ss}", until);

    public static SubtitleRateLimitException QuotaReached(DateTimeOffset until) =>
        new($"OpenSubtitles download quota reached; downloads paused until {until.ToLocalTime():g}", until);
}

/// <summary>What we know about the video we need subtitles for.</summary>
public sealed record SubtitleQuery
{
    public MediaType MediaType { get; init; }
    /// <summary>Movie title or series title.</summary>
    public required string Title { get; init; }
    public int Year { get; init; }
    /// <summary>Movie IMDb id, or the series' IMDb id for episodes.</summary>
    public string? ImdbId { get; init; }
    public int? TmdbId { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public required IReadOnlyList<string> Languages { get; init; }
    public string? FileHash { get; init; }
    public long FileSize { get; init; }
}

public sealed record SubtitleCandidate
{
    public required string Provider { get; init; }
    /// <summary>Provider specific download reference (e.g. OpenSubtitles file id).</summary>
    public required string Payload { get; init; }
    public required string Language { get; init; }
    public IReadOnlyList<string> ReleaseNames { get; init; } = [];
    public string? FileName { get; init; }
    public bool HearingImpaired { get; init; }
    public bool Forced { get; init; }
    public bool HashMatch { get; init; }
    public bool AiTranslated { get; init; }
    public bool TrustedUploader { get; init; }
    public int Downloads { get; init; }
    /// <summary>Season/episode the provider says the subtitle belongs to (episodes only).</summary>
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public string? FeatureTitle { get; init; }
    public int? FeatureYear { get; init; }
    /// <summary>True when the provider matched by an id (IMDb/TMDB) rather than by text.</summary>
    public bool MatchedById { get; init; }
}

public sealed record SubtitleContent(byte[] Data, string Extension);

/// <summary>A subtitle source. Implementations must be safe to call concurrently.</summary>
public interface ISubtitleProvider
{
    string Name { get; }
    bool IsConfigured(SubtitleSettings settings);
    Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleQuery query, SubtitleSettings settings, CancellationToken ct = default);
    Task<SubtitleContent> DownloadAsync(SubtitleCandidate candidate, SubtitleSettings settings, CancellationToken ct = default);
    /// <summary>
    /// While set, the provider asked us to back off (rate limit or download quota) and automatic searches should wait
    /// until then instead of querying it.
    /// </summary>
    DateTimeOffset? BackoffUntil => null;
    /// <summary>Checks credentials/connectivity; returns a human readable status.</summary>
    Task<string> TestAsync(SubtitleSettings settings, CancellationToken ct = default);
}
