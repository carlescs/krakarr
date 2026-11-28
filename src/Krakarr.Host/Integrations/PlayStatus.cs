using System.Text.Json;
using System.Xml;
using Krakarr.Core.Configuration;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Integrations;

/// <summary>
/// A movie's or episode's play state on one media server. <see cref="Progress"/> (0–1) is set while it is part-way
/// watched and not marked played.
/// </summary>
public sealed record PlayState(string Server, bool Played, int PlayCount, double? Progress, DateTimeOffset? LastPlayed)
{
    /// <summary>The play state of a matched item, or an unplayed one when the server sent no user data.</summary>
    public static PlayState From(string server, JellyfinItem item)
    {
        var data = item.UserData;
        if (data is null) return new(server, false, 0, null, null);
        double? progress = !data.Played && data.PlaybackPositionTicks > 0 && item.RunTimeTicks is > 0
            ? Math.Clamp((double)data.PlaybackPositionTicks / item.RunTimeTicks.Value, 0, 1)
            : null;
        return new(server, data.Played, data.PlayCount, progress, data.LastPlayedDate);
    }
}

/// <summary>An episode that is watched or part-way watched on a server (unwatched ones are left out to keep the list short).</summary>
public sealed record EpisodePlayState(int EpisodeId, bool Played, double? Progress, DateTimeOffset? LastPlayed);

/// <summary>
/// A series' play state on one server: of the <see cref="Total"/> downloaded episodes the server has, <see cref="Watched"/>
/// are played. <see cref="Episodes"/> lists the played and part-way watched ones.
/// </summary>
public sealed record SeriesPlayStatus(string Server, int Watched, int Total, List<EpisodePlayState> Episodes);

/// <summary>
/// Reads watched state and progress from the media servers set up for it: Jellyfin and Emby for the chosen user
/// (<see cref="JellyfinSettings.PlayStatusUserId"/>), Plex for the token owner (<see cref="PlexSettings.PlayStatus"/>).
/// Servers are asked in parallel; one that fails or doesn't have the title is just left out.
/// </summary>
public sealed class PlayStatusService(
    KrakarrDbContext db,
    ISettingsService settings,
    JellyfinClient mediaBrowser,
    PlexClient plex,
    ILogger<PlayStatusService> logger)
{
    private sealed record Source(
        string Name,
        Func<JellyfinLookup, CancellationToken, Task<JellyfinItem?>> Find,
        Func<JellyfinItem, CancellationToken, Task<IReadOnlyList<JellyfinItem>>> Episodes);

    /// <summary>The movie's play state on each server that has it; null when the movie doesn't exist.</summary>
    public async Task<IReadOnlyList<PlayState>?> MovieAsync(int id, CancellationToken ct)
    {
        var movie = await db.Movies.AsNoTracking().Where(m => m.Id == id)
            .Select(m => new { m.Title, m.OriginalTitle, m.Year, m.TmdbId, m.ImdbId, HasFile = m.MovieFileId != null })
            .FirstOrDefaultAsync(ct);
        if (movie is null) return null;
        if (!movie.HasFile) return [];
        var sources = await SourcesAsync(ct);
        if (sources.Count == 0) return [];

        var lookup = new JellyfinLookup(JellyfinItemKind.Movie, movie.Title, movie.OriginalTitle, movie.Year, movie.TmdbId, null, movie.ImdbId);
        var results = await Task.WhenAll(sources.Select(source => TryAsync(source.Name, lookup.Title, async () =>
            await source.Find(lookup, ct) is { } item ? PlayState.From(source.Name, item) : null, ct)));
        return results.OfType<PlayState>().ToList();
    }

    /// <summary>The series' play state on each server that has it; null when the series doesn't exist.</summary>
    public async Task<IReadOnlyList<SeriesPlayStatus>?> SeriesAsync(int id, CancellationToken ct)
    {
        var series = await db.Series.AsNoTracking().Where(s => s.Id == id)
            .Select(s => new { Title = s.MetadataTitle != "" ? s.MetadataTitle : s.Title, s.Year, s.TmdbId, s.TvdbId, s.ImdbId })
            .FirstOrDefaultAsync(ct);
        if (series is null) return null;
        var sources = await SourcesAsync(ct);
        if (sources.Count == 0) return [];

        // Only downloaded episodes can have been watched from the library: slim rows, one series.
        var episodes = await db.Episodes.AsNoTracking().Where(e => e.SeriesId == id && e.EpisodeFileId != null)
            .Select(e => new { e.Id, e.SeasonNumber, e.EpisodeNumber, e.TmdbId, e.TvdbId })
            .ToListAsync(ct);
        if (episodes.Count == 0) return [];
        var lookups = episodes.Select(e => new JellyfinEpisodeLookup(e.SeasonNumber, e.EpisodeNumber, e.TmdbId, e.TvdbId)).ToList();

        var lookup = new JellyfinLookup(JellyfinItemKind.Series, series.Title, null, series.Year, series.TmdbId, series.TvdbId, series.ImdbId);
        var results = await Task.WhenAll(sources.Select(source => TryAsync(source.Name, lookup.Title, async () =>
        {
            if (await source.Find(lookup, ct) is not { } show) return null;
            var matches = JellyfinClient.MatchEpisodes(await source.Episodes(show, ct), lookups);
            var states = new List<EpisodePlayState>();
            int total = 0, watched = 0;
            for (var i = 0; i < matches.Length; i++)
            {
                if (matches[i] is not { } item) continue;
                total++;
                var state = PlayState.From(source.Name, item);
                if (state.Played) watched++;
                if (state.Played || state.Progress is not null)
                    states.Add(new(episodes[i].Id, state.Played, state.Progress, state.LastPlayed));
            }
            return new SeriesPlayStatus(source.Name, watched, total, states);
        }, ct)));
        return results.OfType<SeriesPlayStatus>().ToList();
    }

    private async Task<List<Source>> SourcesAsync(CancellationToken ct)
    {
        var sources = new List<Source>();
        var jellyfin = await settings.GetAsync<JellyfinSettings>(ct);
        if (MediaBrowserServer.From(jellyfin) is { } j && Trimmed(jellyfin.PlayStatusUserId) is { } jUser)
            sources.Add(MediaBrowser("Jellyfin", j, jUser));
        var emby = await settings.GetAsync<EmbySettings>(ct);
        if (MediaBrowserServer.From(emby) is { } e && Trimmed(emby.PlayStatusUserId) is { } eUser)
            sources.Add(MediaBrowser("Emby", e, eUser));
        var plexSettings = await settings.GetAsync<PlexSettings>(ct);
        if (plexSettings is { IsConfigured: true, PlayStatus: true })
            sources.Add(new("Plex", (lookup, c) => plex.FindItemAsync(plexSettings, lookup, c), async (show, c) => await plex.EpisodesAsync(plexSettings, show.Id, c)));
        return sources;
    }

    private Source MediaBrowser(string name, MediaBrowserServer server, string userId) =>
        new(name, (lookup, c) => mediaBrowser.FindUserItemAsync(server, lookup, userId, c), (show, c) => mediaBrowser.EpisodesAsync(server, show.Id, userId, c));

    private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async Task<T?> TryAsync<T>(string server, string title, Func<Task<T?>> read, CancellationToken ct) where T : class
    {
        try
        {
            return await read();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or XmlException or UriFormatException or InvalidOperationException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // Play status is a convenience: an unreachable server is just left out.
            logger.LogWarning("Cannot read the play status of {Title} from {Server}: {Error}", title, server, ex.Message);
            return null;
        }
    }
}
