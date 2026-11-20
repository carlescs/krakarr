using Krakarr.Core.Configuration;
using Krakarr.Core.Metadata;
using Krakarr.Data;

namespace Krakarr.Series;

/// <summary>A show found by a search. The id of the active metadata source is always set; the other one only when known.</summary>
public sealed record SeriesLookup
{
    public int? TmdbId { get; init; }
    public int? TvdbId { get; init; }
    public required string Title { get; init; }
    public int Year { get; init; }
    public string? Overview { get; init; }
    public string? PosterUrl { get; init; }
    public string? Network { get; init; }
    public string? Status { get; init; }
    /// <summary>TMDB average user score (0-10); only set by discovery lists.</summary>
    public double? Rating { get; init; }
    /// <summary>Local series id when the show is already in the library.</summary>
    public int? SeriesId { get; init; }
}

public sealed record EpisodeInfo
{
    public int? TmdbId { get; init; }
    public int? TvdbId { get; init; }
    public int SeasonNumber { get; init; }
    public int EpisodeNumber { get; init; }
    public int? AbsoluteNumber { get; init; }
    public string? Title { get; init; }
    public string? Overview { get; init; }
    public DateOnly? AirDate { get; init; }
    public int Runtime { get; init; }
}

public sealed record SeriesInfo
{
    public int? TmdbId { get; init; }
    /// <summary>TheTVDB id; indexers search and tag TV releases by it.</summary>
    public int? TvdbId { get; init; }
    public required string Title { get; init; }
    public int Year { get; init; }
    public string? Overview { get; init; }
    public string? PosterUrl { get; init; }
    public string? FanartUrl { get; init; }
    public string? Network { get; init; }
    /// <summary>Normalised to "Continuing", "Ended" or "Upcoming".</summary>
    public string? Status { get; init; }
    public int Runtime { get; init; }
    public string? ImdbId { get; init; }
    public DateOnly? FirstAired { get; init; }
    public IReadOnlyList<EpisodeInfo> Episodes { get; init; } = [];
}

/// <summary>Ids a series is known by; at least one is set.</summary>
public sealed record SeriesIds(int? TmdbId, int? TvdbId)
{
    public bool IsEmpty => TmdbId is null && TvdbId is null;
}

/// <summary>Series metadata from the source chosen in Settings → Metadata (TMDB or TheTVDB).</summary>
public interface ISeriesMetadata
{
    Task<SeriesMetadataSource> SourceAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SeriesLookup>> SearchAsync(string query, CancellationToken ct = default);
    /// <summary>Full show with episodes. A missing id for the active source is resolved through the other one.</summary>
    Task<SeriesInfo> GetSeriesAsync(SeriesIds ids, CancellationToken ct = default);
}

public sealed class SeriesMetadata(ISettingsService settings, ITmdbTvClient tmdb, ITvdbClient tvdb) : ISeriesMetadata
{
    public async Task<SeriesMetadataSource> SourceAsync(CancellationToken ct = default) =>
        (await settings.GetAsync<MetadataSettings>(ct)).SeriesSource;

    public async Task<IReadOnlyList<SeriesLookup>> SearchAsync(string query, CancellationToken ct = default) =>
        await SourceAsync(ct) == SeriesMetadataSource.Tvdb ? await tvdb.SearchAsync(query, ct) : await tmdb.SearchAsync(query, ct);

    public async Task<SeriesInfo> GetSeriesAsync(SeriesIds ids, CancellationToken ct = default)
    {
        if (ids.IsEmpty) throw new MetadataException("A TMDB or TheTVDB id is required");

        if (await SourceAsync(ct) == SeriesMetadataSource.Tvdb)
        {
            var tvdbId = ids.TvdbId ?? await tmdb.GetTvdbIdAsync(ids.TmdbId!.Value, ct)
                ?? throw new MetadataException($"TMDB show {ids.TmdbId} has no TheTVDB id");
            var info = await tvdb.GetSeriesAsync(tvdbId, ct);
            return info.TmdbId is null && ids.TmdbId is not null ? info with { TmdbId = ids.TmdbId } : info;
        }

        var tmdbId = ids.TmdbId ?? (await tmdb.FindByTvdbAsync(ids.TvdbId!.Value, ct))?.TmdbId
            ?? throw new MetadataException($"No TMDB show found for TheTVDB id {ids.TvdbId}");
        var show = await tmdb.GetSeriesAsync(tmdbId, ct);
        return show.TvdbId is null && ids.TvdbId is not null ? show with { TvdbId = ids.TvdbId } : show;
    }
}
