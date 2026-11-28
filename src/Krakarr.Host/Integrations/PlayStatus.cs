using System.Text.Json;
using System.Xml;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
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
/// A library title played on a media server. For a series, <see cref="SeasonNumber"/> and <see cref="EpisodeNumber"/>
/// are the last episode played. <see cref="Finished"/> is false while it was only played part-way.
/// </summary>
public sealed record RecentlyPlayedItem(MediaType MediaType, int Id, string Title, int Year, string? PosterUrl, DateTimeOffset Date,
    string Server, int? SeasonNumber, int? EpisodeNumber, bool Finished);

/// <summary>What was played last; <see cref="Enabled"/> is false when no media server is set up to report it.</summary>
public sealed record RecentlyPlayed(bool Enabled, IReadOnlyList<RecentlyPlayedItem> Items);

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
    /// <summary>Plays read from each server: room for a few episodes of the same show before it fills the list.</summary>
    internal const int RecentPlaysPerServer = 60;

    private sealed record Source(
        string Name,
        Func<JellyfinLookup, CancellationToken, Task<JellyfinItem?>> Find,
        Func<JellyfinItem, CancellationToken, Task<IReadOnlyList<JellyfinItem>>> Episodes,
        Func<int, CancellationToken, Task<IReadOnlyList<RecentPlay>>> Recent);

    /// <summary>
    /// The library titles played last on any server set up to report play status, newest first and one entry per title
    /// (a series shows its latest episode). Plays of titles not in the library are left out.
    /// </summary>
    public async Task<RecentlyPlayed> RecentlyPlayedAsync(int limit, CancellationToken ct)
    {
        var sources = await SourcesAsync(ct);
        if (sources.Count == 0) return new(false, []);

        var perServer = await Task.WhenAll(sources.Select(source => TryAsync(source.Name, "recently played", async () =>
            (await source.Recent(RecentPlaysPerServer, ct)).Select(p => (Server: source.Name, Play: p)).ToList(), ct)));
        var plays = perServer.OfType<List<(string Server, RecentPlay Play)>>().SelectMany(p => p).OrderByDescending(p => p.Play.Played).ToList();
        if (plays.Count == 0) return new(true, []);

        // Match by provider id: one query per kind, bounded by the plays read.
        var movieTmdb = Ids(plays, JellyfinItemKind.Movie, "Tmdb").Select(ParseInt).OfType<int>().ToList();
        var movieImdb = Ids(plays, JellyfinItemKind.Movie, "Imdb").ToList();
        var seriesTvdb = Ids(plays, JellyfinItemKind.Series, "Tvdb").Select(ParseInt).OfType<int>().ToList();
        var seriesTmdb = Ids(plays, JellyfinItemKind.Series, "Tmdb").Select(ParseInt).OfType<int>().ToList();
        var seriesImdb = Ids(plays, JellyfinItemKind.Series, "Imdb").ToList();

        var movies = movieTmdb.Count + movieImdb.Count == 0 ? [] : await db.Movies.AsNoTracking()
            .Where(m => movieTmdb.Contains(m.TmdbId) || (m.ImdbId != null && movieImdb.Contains(m.ImdbId)))
            .Select(m => new Title(m.Id, m.Title, m.Year, m.PosterUrl, m.TmdbId, null, m.ImdbId))
            .ToListAsync(ct);
        var shows = seriesTvdb.Count + seriesTmdb.Count + seriesImdb.Count == 0 ? [] : await db.Series.AsNoTracking()
            .Where(s => (s.TvdbId != null && seriesTvdb.Contains(s.TvdbId.Value)) || (s.TmdbId != null && seriesTmdb.Contains(s.TmdbId.Value))
                || (s.ImdbId != null && seriesImdb.Contains(s.ImdbId)))
            .Select(s => new Title(s.Id, s.Title, s.Year, s.PosterUrl, s.TmdbId, s.TvdbId, s.ImdbId))
            .ToListAsync(ct);

        var result = new List<RecentlyPlayedItem>(limit);
        var seen = new HashSet<(MediaType, int)>();
        foreach (var (server, play) in plays)
        {
            var movie = play.Kind == JellyfinItemKind.Movie;
            if ((movie ? Find(movies, play) : Find(shows, play)) is not { } title) continue;
            var type = movie ? MediaType.Movie : MediaType.Series;
            if (!seen.Add((type, title.Id))) continue;
            result.Add(new(type, title.Id, title.Name, title.Year, title.PosterUrl, play.Played, server, play.SeasonNumber, play.EpisodeNumber, play.Finished));
            if (result.Count >= limit) break;
        }
        return new(true, result);
    }

    private sealed record Title(int Id, string Name, int Year, string? PosterUrl, int? TmdbId, int? TvdbId, string? ImdbId);

    private static Title? Find(List<Title> titles, RecentPlay play) =>
        titles.FirstOrDefault(t => SameId(play, "Tvdb", t.TvdbId?.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        ?? titles.FirstOrDefault(t => SameId(play, "Tmdb", t.TmdbId?.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        ?? titles.FirstOrDefault(t => SameId(play, "Imdb", t.ImdbId));

    private static bool SameId(RecentPlay play, string provider, string? ours) =>
        !string.IsNullOrEmpty(ours) && ProviderId(play, provider) is { } theirs && string.Equals(theirs, ours, StringComparison.OrdinalIgnoreCase);

    private static string? ProviderId(RecentPlay play, string provider) =>
        play.ProviderIds.FirstOrDefault(p => string.Equals(p.Key, provider, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } v ? v.Trim() : null;

    private static IEnumerable<string> Ids(List<(string Server, RecentPlay Play)> plays, JellyfinItemKind kind, string provider) =>
        plays.Where(p => p.Play.Kind == kind).Select(p => ProviderId(p.Play, provider)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);

    private static int? ParseInt(string s) => int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

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
            sources.Add(new("Plex", (lookup, c) => plex.FindItemAsync(plexSettings, lookup, c), async (show, c) => await plex.EpisodesAsync(plexSettings, show.Id, c),
                (limit, c) => plex.RecentlyPlayedAsync(plexSettings, limit, c)));
        return sources;
    }

    private Source MediaBrowser(string name, MediaBrowserServer server, string userId) =>
        new(name, (lookup, c) => mediaBrowser.FindUserItemAsync(server, lookup, userId, c), (show, c) => mediaBrowser.EpisodesAsync(server, show.Id, userId, c),
            (limit, c) => mediaBrowser.RecentlyPlayedAsync(server, userId, limit, c));

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
