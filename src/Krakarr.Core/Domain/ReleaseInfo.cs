namespace Krakarr.Core.Domain;

/// <summary>A release as returned by an indexer search or RSS feed.</summary>
public sealed record ReleaseInfo
{
    public required string Title { get; init; }
    public required string Guid { get; init; }
    public string? DownloadUrl { get; init; }
    public string? MagnetUrl { get; init; }
    public string? InfoHash { get; init; }
    public string? InfoUrl { get; init; }
    public long Size { get; init; }
    public int? Seeders { get; init; }
    public int? Peers { get; init; }
    public DateTimeOffset? PublishDate { get; init; }
    public int? ImdbId { get; init; }
    public int? TmdbId { get; init; }
    public int? TvdbId { get; init; }
    public IReadOnlyList<int> Categories { get; init; } = [];
    public int IndexerId { get; init; }
    public string IndexerName { get; init; } = string.Empty;
    public int IndexerPriority { get; init; } = 25;
}
