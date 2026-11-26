using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Core.Jobs;
using Krakarr.Data;
using Krakarr.Subtitles.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Subtitles;

public sealed record SubtitleFileDto(int Id, int? MovieFileId, int? EpisodeFileId, string Language, string LanguageName, bool HearingImpaired, bool Forced,
    string FileName, string Provider, int? Score, string? Release, DateTimeOffset DateAdded,
    bool Manual = false, DateTimeOffset? SyncedAt = null, string? SyncNote = null);

/// <summary>Which sync tools were found; with none, subtitles can still be synced against a reference subtitle or shifted by hand.</summary>
public sealed record SubtitleSyncToolsDto(bool Alass, bool Ffsubsync, bool Ffmpeg);
public sealed record SubtitleShiftRequest(int OffsetMs);

public sealed record SubtitleProviderDto(string Name, string DisplayName, bool Configured);
/// <param name="NotFound">Searching all wanted files only: true = only files marked "not found", false = only unmarked files, null = all.</param>
public sealed record SubtitleSearchRequest(int? MovieFileId, int? EpisodeFileId, int? MovieId = null, int? SeriesId = null, IReadOnlyList<string>? Providers = null, bool? NotFound = null);
/// <summary>Files missing a wanted language: still searched automatically, and marked "not found".</summary>
public sealed record WantedSubtitleCountsDto(int Searching, int NotFound);
/// <summary>Movie or episode file to search automatically again (or to mark "not found"); neither means every file.</summary>
public sealed record SubtitleRetryRequest(int? MovieFileId, int? EpisodeFileId = null);
/// <param name="Manual">Picked by hand from the full list (default): automatic upgrades leave it alone. Best-match downloads send false so a better subtitle can still replace them.</param>
public sealed record SubtitleDownloadRequest(int? MovieFileId, int? EpisodeFileId, SubtitleCandidate Candidate, int? Score, bool Manual = true);
/// <param name="Best">Interactive search only: the top pick for its language even below the minimum score, i.e. what "best match" offers.</param>
public sealed record ScoredSubtitleDto(SubtitleCandidate Candidate, int Score, IReadOnlyList<string> Matches, IReadOnlyList<string> Rejections, bool Acceptable, bool Best = false);
/// <summary>Best subtitle per missing language, even below <c>MinimumScore</c>; downloaded only once the user confirms.</summary>
public sealed record BestSubtitleMatchesDto(string Label, IReadOnlyList<string> MissingLanguages, int MinimumScore, IReadOnlyList<ScoredSubtitleDto> Results, IReadOnlyList<string> Errors);
/// <summary>The subtitle languages Krakarr looks for, so detail pages can show which ones a file is missing.</summary>
public sealed record WantedSubtitleLanguagesDto(bool Enabled, bool UseEmbeddedSubtitles, IReadOnlyList<string> Languages);
public sealed record WantedSubtitleDto(string MediaType, int? MovieId, int? SeriesId, int? MovieFileId, int? EpisodeFileId, string Label, IReadOnlyList<string> MissingLanguages, DateTimeOffset? NotFound);
/// <summary>A page of the wanted list. <c>Total</c> counts the selected filter, <c>LibraryTotal</c> every wanted file.</summary>
public sealed record WantedSubtitlePageDto(IReadOnlyList<WantedSubtitleDto> Items, int Page, int PageSize, int Total, int LibraryTotal, int Searching, int NotFound);

public sealed record EmbeddedSubtitleDto(string Language, string LanguageName, bool HearingImpaired, bool Forced);
/// <summary>An external subtitle in a language the video already has as an embedded track; it can usually be deleted.</summary>
public sealed record RedundantSubtitleDto(int Id, string MediaType, int? MovieId, int? SeriesId, int? MovieFileId, int? EpisodeFileId, string Label,
    string Language, string LanguageName, bool HearingImpaired, bool Forced, string FileName, string Provider, DateTimeOffset DateAdded,
    IReadOnlyList<EmbeddedSubtitleDto> Embedded);
public sealed record RedundantSubtitlePageDto(IReadOnlyList<RedundantSubtitleDto> Items, int Offset, int Limit, int Total);

public static class SubtitleEndpoints
{
    public static IEndpointRouteBuilder MapSubtitleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/subtitles").WithTags("Subtitles");

        group.MapGet("/languages", () => Languages.All.Select(l => new { l.Code, l.Name }));

        group.MapGet("/providers", async (SubtitleService service, ISettingsService settings, CancellationToken ct) =>
        {
            var s = await settings.GetAsync<SubtitleSettings>(ct);
            return service.Providers.Select(p => new SubtitleProviderDto(p.Name, p.DisplayName, p.IsConfigured(s)));
        });

        group.MapPost("/providers/test", async (SubtitleSettings input, SubtitleService service, ISettingsService settings, CancellationToken ct) =>
        {
            if (input.OpenSubtitles.Password == SubtitleSettings.PasswordMask)
            {
                var stored = await settings.GetAsync<SubtitleSettings>(ct);
                input = input with { OpenSubtitles = input.OpenSubtitles with { Password = stored.OpenSubtitles.Password } };
            }

            var results = new List<object>();
            foreach (var provider in service.Providers)
            {
                try
                {
                    results.Add(new { provider.Name, Success = true, Message = await provider.TestAsync(input, ct) });
                }
                catch (SubtitleProviderException ex)
                {
                    results.Add(new { provider.Name, Success = false, ex.Message });
                }
            }

            return results;
        });

        // Subtitle files for a movie, a whole series or one video file, plus the subtitle tracks inside the videos (Provider "embedded", Id 0).
        group.MapGet("/files", async (int? movieId, int? seriesId, int? movieFileId, int? episodeFileId, KrakarrDbContext db, CancellationToken ct) =>
        {
            IQueryable<SubtitleFile> query;
            List<SubtitleFileDto> embedded;
            if (movieFileId is { } mfid)
            {
                query = db.SubtitleFiles.Where(s => s.MovieFileId == mfid);
                var files = await db.MovieFiles.AsNoTracking().Where(f => f.Id == mfid && f.EmbeddedSubtitles != null && f.EmbeddedSubtitles != "")
                    .Select(f => new { f.Id, f.RelativePath, f.EmbeddedSubtitles, f.DateAdded }).ToListAsync(ct);
                embedded = files.SelectMany(f => EmbeddedDtos(f.Id, null, f.RelativePath, f.EmbeddedSubtitles, f.DateAdded)).ToList();
            }
            else if (episodeFileId is { } efid)
            {
                query = db.SubtitleFiles.Where(s => s.EpisodeFileId == efid);
                var files = await db.EpisodeFiles.AsNoTracking().Where(f => f.Id == efid && f.EmbeddedSubtitles != null && f.EmbeddedSubtitles != "")
                    .Select(f => new { f.Id, f.RelativePath, f.EmbeddedSubtitles, f.DateAdded }).ToListAsync(ct);
                embedded = files.SelectMany(f => EmbeddedDtos(null, f.Id, f.RelativePath, f.EmbeddedSubtitles, f.DateAdded)).ToList();
            }
            else if (movieId is { } mid)
            {
                var fileIds = db.MovieFiles.Where(f => f.MovieId == mid).Select(f => f.Id);
                query = db.SubtitleFiles.Where(s => s.MovieFileId != null && fileIds.Contains(s.MovieFileId.Value));
                var files = await db.MovieFiles.AsNoTracking().Where(f => f.MovieId == mid && f.EmbeddedSubtitles != null && f.EmbeddedSubtitles != "")
                    .Select(f => new { f.Id, f.RelativePath, f.EmbeddedSubtitles, f.DateAdded }).ToListAsync(ct);
                embedded = files.SelectMany(f => EmbeddedDtos(f.Id, null, f.RelativePath, f.EmbeddedSubtitles, f.DateAdded)).ToList();
            }
            else if (seriesId is { } sid)
            {
                var fileIds = db.EpisodeFiles.Where(f => f.SeriesId == sid).Select(f => f.Id);
                query = db.SubtitleFiles.Where(s => s.EpisodeFileId != null && fileIds.Contains(s.EpisodeFileId.Value));
                var files = await db.EpisodeFiles.AsNoTracking().Where(f => f.SeriesId == sid && f.EmbeddedSubtitles != null && f.EmbeddedSubtitles != "")
                    .Select(f => new { f.Id, f.RelativePath, f.EmbeddedSubtitles, f.DateAdded }).ToListAsync(ct);
                embedded = files.SelectMany(f => EmbeddedDtos(null, f.Id, f.RelativePath, f.EmbeddedSubtitles, f.DateAdded)).ToList();
            }
            else
            {
                return Results.Problem("movieId, seriesId, movieFileId or episodeFileId is required", statusCode: 400);
            }

            var rows = await query.AsNoTracking().OrderBy(s => s.Language).ToListAsync(ct);
            return Results.Ok(rows.Select(ToDto).Concat(embedded).OrderBy(d => d.Language, StringComparer.Ordinal));
        });

        group.MapGet("/wanted-languages", async (ISettingsService settings, CancellationToken ct) =>
        {
            var s = await settings.GetAsync<SubtitleSettings>(ct);
            return new WantedSubtitleLanguagesDto(s.Enabled, s.UseEmbeddedSubtitles, s.Languages);
        });

        // notFound: true = only files no provider had subtitles for, false = only files still searched automatically.
        // sort: type, title or missing (default: movies first, then episodes by series and season).
        group.MapGet("/wanted", async (int? page, int? pageSize, bool? notFound, string? sort, bool? desc, SubtitleService service, CancellationToken ct) =>
        {
            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 60, 1, 500);
            var order = Enum.TryParse<WantedSort>(sort, ignoreCase: true, out var parsed) ? parsed : WantedSort.Default;
            var result = await service.WantedPageAsync(p, size, ct, notFound, order, desc ?? false);
            var items = result.Items.Select(w => new WantedSubtitleDto(
                w.Target.MediaType.ToString(), w.Target.MovieId, w.Target.SeriesId, w.Target.MovieFileId, w.Target.EpisodeFileId, w.Target.Label, w.MissingLanguages, w.NotFound)).ToList();
            return new WantedSubtitlePageDto(items, p, size, result.Total, result.Searching + result.NotFound, result.Searching, result.NotFound);
        });

        // External subtitles that duplicate an embedded track (same language and forced flag), sorted by title.
        group.MapGet("/redundant", async (int? offset, int? limit, SubtitleService service, CancellationToken ct) =>
        {
            var skip = Math.Max(0, offset ?? 0);
            var size = Math.Clamp(limit ?? 100, 1, 500);
            var result = await service.RedundantPageAsync(skip, size, ct);
            var items = result.Items.Select(r => new RedundantSubtitleDto(
                r.Id, r.MediaType.ToString(), r.MovieId, r.SeriesId, r.MovieFileId, r.EpisodeFileId, r.Label,
                r.Language, Languages.Name(r.Language), r.HearingImpaired, r.Forced, Path.GetFileName(r.Path), r.Provider, r.DateAdded,
                r.Embedded.Select(e => new EmbeddedSubtitleDto(e.Language, Languages.Name(e.Language), e.HearingImpaired, e.Forced)).ToList())).ToList();
            return new RedundantSubtitlePageDto(items, skip, size, result.Total);
        });

        group.MapGet("/wanted/counts", async (SubtitleService service, CancellationToken ct) =>
        {
            var (searching, notFound) = await service.WantedCountsAsync(ct);
            return new WantedSubtitleCountsDto(searching, notFound);
        });

        // Clears the "not found" mark so the scheduled search picks the file(s) up again on its next run.
        group.MapPost("/retry", async (SubtitleRetryRequest req, SubtitleService service, CancellationToken ct) =>
            Results.Ok(new { count = await service.RetryNotFoundAsync(req.MovieFileId, ct, req.EpisodeFileId) }));

        // Marks one movie or episode file "not found", or every file still missing a wanted language when neither is
        // given: the scheduled search stops retrying them until they are retried.
        group.MapPost("/not-found", async (SubtitleRetryRequest? req, SubtitleService service, CancellationToken ct) =>
            Results.Ok(new
            {
                count = req is { MovieFileId: not null } or { EpisodeFileId: not null }
                    ? await service.MarkFileNotFoundAsync(req.MovieFileId, req.EpisodeFileId, ct)
                    : await service.MarkAllNotFoundAsync(ct),
            }));

        // Automatic search for one file (every wanted language, upgrading existing subtitles), or all wanted files
        // (missing languages only) when no id is given.
        group.MapPost("/search", async (SubtitleSearchRequest req, SubtitleService service, KrakarrDbContext db, ICommandQueue commands, CancellationToken ct) =>
        {
            List<string>? selectedProviders = null;
            List<string>? selectedDisplayNames = null;
            if (req.Providers is { Count: > 0 })
            {
                var known = service.Providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
                var normalized = new List<string>();
                var displayNames = new List<string>();
                var unknown = new List<string>();
                foreach (var raw in req.Providers)
                {
                    var name = raw?.Trim();
                    if (string.IsNullOrEmpty(name)) continue;
                    if (known.TryGetValue(name, out var provider))
                    {
                        if (!normalized.Contains(provider.Name, StringComparer.OrdinalIgnoreCase))
                        {
                            normalized.Add(provider.Name);
                            displayNames.Add(provider.DisplayName);
                        }
                    }
                    else if (!unknown.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        unknown.Add(name);
                    }
                }

                if (unknown.Count > 0)
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["providers"] = [$"Unknown subtitle provider(s): {string.Join(", ", unknown)}"],
                    });

                if (normalized.Count > 0)
                {
                    selectedProviders = normalized;
                    selectedDisplayNames = displayNames;
                }
            }

            // A whole movie or series from its detail page: every file, every wanted language.
            if (req.MovieId is { } movieId)
            {
                var fileId = await db.MovieFiles.AsNoTracking().Where(f => f.MovieId == movieId).OrderBy(f => f.Id).Select(f => (int?)f.Id).FirstOrDefaultAsync(ct);
                if (fileId is null) return Results.NotFound();
                req = req with { MovieFileId = fileId, MovieId = null };
            }
            else if (req.SeriesId is { } seriesId)
            {
                var title = await db.Series.AsNoTracking().Where(s => s.Id == seriesId).Select(s => s.Title).FirstOrDefaultAsync(ct);
                if (title is null) return Results.NotFound();
                var series = commands.Enqueue($"Subtitles: {title}", async (sp, token) =>
                {
                    var svc = sp.GetRequiredService<SubtitleService>();
                    var scopedDb = sp.GetRequiredService<KrakarrDbContext>();
                    var ids = await scopedDb.EpisodeFiles.AsNoTracking().Where(f => f.SeriesId == seriesId)
                        .OrderBy(f => f.SeasonNumber).ThenBy(f => f.Id).Select(f => f.Id).ToListAsync(token);
                    foreach (var id in ids)
                    {
                        if (await svc.GetEpisodeTargetAsync(id, token) is { } t) await svc.SearchMissingAsync(t, token, upgrade: true, providers: selectedProviders);
                        scopedDb.ChangeTracker.Clear();
                    }
                    return $"{title}: searched {ids.Count} episode file(s)";
                });
                return Results.Accepted(value: series);
            }

            if (req.MovieFileId is null && req.EpisodeFileId is null)
            {
                var commandName = selectedDisplayNames is { Count: > 0 }
                    ? $"Search all missing subtitles ({string.Join(", ", selectedDisplayNames)})"
                    : "Search all missing subtitles";
                var command = commands.Enqueue(commandName, async (sp, token) =>
                {
                    var svc = sp.GetRequiredService<SubtitleService>();
                    var scopedDb = sp.GetRequiredService<KrakarrDbContext>();
                    var wanted = await svc.WantedAsync(100, token, req.NotFound);
                    foreach (var w in wanted)
                    {
                        await svc.SearchMissingAsync(w.Target, token, providers: selectedProviders);
                        scopedDb.ChangeTracker.Clear();
                    }
                    return $"Searched {wanted.Count} file(s)";
                });
                return Results.Accepted(value: command);
            }

            var target = await ResolveAsync(service, req.MovieFileId, req.EpisodeFileId, ct);
            if (target is null) return Results.NotFound();
            var single = commands.Enqueue($"Subtitles: {target.Label}", async (sp, token) =>
            {
                var svc = sp.GetRequiredService<SubtitleService>();
                var t = await ResolveAsync(svc, req.MovieFileId, req.EpisodeFileId, token) ?? throw new KeyNotFoundException();
                return await svc.SearchMissingAsync(t, token, upgrade: true, providers: selectedProviders);
            });
            return Results.Accepted(value: single);
        });

        // Interactive search: every candidate with its score and why it would be skipped automatically.
        // `language` narrows the search to one language or a comma-separated few (e.g. the ones the file is missing).
        group.MapGet("/candidates", async (int? movieFileId, int? episodeFileId, string? language, SubtitleService service, ISettingsService settings, CancellationToken ct) =>
        {
            var target = await ResolveAsync(service, movieFileId, episodeFileId, ct);
            if (target is null) return Results.NotFound();
            var s = await settings.GetAsync<SubtitleSettings>(ct);
            var requested = (language ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => Languages.Normalize(l) ?? l).Distinct(StringComparer.Ordinal).Take(20).ToList();
            IReadOnlyList<string> languages = requested.Count > 0 ? requested : s.Languages;
            var (candidates, errors) = await service.SearchProvidersAsync(target, languages, s, ct);
            // Per language, what "best match" would offer: the top candidate ignoring the minimum score (other rules still apply).
            var best = languages
                .Select(l => SubtitleScorer.Rank(target.Video, candidates.Where(c => c.Language == l), s, ignoreMinimum: true)
                    .FirstOrDefault(r => r.Acceptable && r.Score > 0)?.Candidate)
                .OfType<SubtitleCandidate>()
                .Select(c => (c.Provider, c.Payload))
                .ToHashSet();
            var ranked = languages.SelectMany(l => SubtitleScorer.Rank(target.Video, candidates.Where(c => c.Language == l), s))
                .OrderByDescending(r => r.Score)
                .Select(r => new ScoredSubtitleDto(r.Candidate, r.Score, r.Matches, r.Rejections, r.Acceptable, best.Contains((r.Candidate.Provider, r.Candidate.Payload))));
            return Results.Ok(new { target.Label, Results = ranked, Errors = errors, MinimumScore = SubtitleScorer.MinimumScore(target.Video, s) });
        });

        // Best match per missing language, ignoring the minimum score; the UI asks before downloading each one.
        group.MapGet("/best-match", async (int? movieFileId, int? episodeFileId, SubtitleService service, CancellationToken ct) =>
        {
            var target = await ResolveAsync(service, movieFileId, episodeFileId, ct);
            if (target is null) return Results.NotFound();
            var best = await service.BestMatchesAsync(target, ct);
            return Results.Ok(new BestSubtitleMatchesDto(target.Label, best.MissingLanguages, best.MinimumScore,
                best.Best.Select(r => new ScoredSubtitleDto(r.Candidate, r.Score, r.Matches, r.Rejections, r.Acceptable)).ToList(), best.Errors));
        });

        group.MapPost("/download", async (SubtitleDownloadRequest req, SubtitleService service, CancellationToken ct) =>
        {
            var target = await ResolveAsync(service, req.MovieFileId, req.EpisodeFileId, ct);
            if (target is null) return Results.NotFound();
            try
            {
                return Results.Ok(ToDto(await service.DownloadAsync(target, req.Candidate, req.Score, ct: ct, manual: req.Manual)));
            }
            catch (SubtitleProviderException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        group.MapGet("/sync/tools", async (SubtitleSyncService sync, CancellationToken ct) =>
        {
            var tools = await sync.ToolsAsync(ct);
            return new SubtitleSyncToolsDto(tools.Alass is not null, tools.Ffsubsync is not null, tools.Ffmpeg is not null);
        });

        // Resyncs one subtitle against the video in the background (it can take minutes); the outcome lands on the row.
        // It shows in the tasks menu of whoever asked for it, as waiting until it starts, then with its progress.
        group.MapPost("/{id:int}/sync", async (int id, HttpContext http, KrakarrDbContext db, SubtitleSyncService sync, SubtitleSyncProgress progress,
            ICommandQueue commands, CancellationToken ct) =>
        {
            var row = await db.SubtitleFiles.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (row is null) return Results.NotFound();
            var (title, link) = await sync.DescribeAsync(row, ct);
            var cancel = progress.Queue(id, title, link, MediaSubscriptions.GetUserId(http.User));
            var command = commands.Enqueue($"Sync subtitle: {Path.GetFileName(row.Path)}", async (sp, token) =>
            {
                try
                {
                    if (cancel.IsCancellationRequested) return SubtitleSyncProgress.CancelledResult; // cancelled while queued
                    var result = await sp.GetRequiredService<SubtitleSyncService>().SyncAsync(id, token) ?? throw new KeyNotFoundException("Subtitle not found");
                    if (result.Success || cancel.IsCancellationRequested) return result.Message;
                    throw new InvalidOperationException(result.Message);
                }
                finally
                {
                    progress.Finish(id);
                }
            });
            return Results.Accepted(value: command);
        });

        // Moves every cue by a fixed amount (negative = earlier).
        group.MapPost("/{id:int}/shift", async (int id, SubtitleShiftRequest req, SubtitleSyncService sync, CancellationToken ct) =>
        {
            if (req.OffsetMs == 0 || Math.Abs(req.OffsetMs) > 3_600_000)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["offsetMs"] = ["Give a shift between -3600000 and 3600000 ms, other than 0"] });
            try
            {
                return await sync.ShiftAsync(id, req.OffsetMs, ct) is { } row ? Results.Ok(ToDto(row)) : Results.NotFound();
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidDataException or UnauthorizedAccessException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        });

        group.MapDelete("/{id:int}", async (int id, KrakarrDbContext db, IEventHub events, CancellationToken ct) =>
        {
            var row = await db.SubtitleFiles.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (row is null) return Results.NotFound();
            if (File.Exists(row.Path)) File.Delete(row.Path);
            db.SubtitleFiles.Remove(row);
            await db.SaveChangesAsync(ct);
            // Detail pages and other open lists refresh; they match on the movie or series id.
            var movieId = row.MovieFileId is { } mf ? await db.MovieFiles.Where(f => f.Id == mf).Select(f => (int?)f.MovieId).FirstOrDefaultAsync(ct) : null;
            var seriesId = row.EpisodeFileId is { } ef ? await db.EpisodeFiles.Where(f => f.Id == ef).Select(f => (int?)f.SeriesId).FirstOrDefaultAsync(ct) : null;
            await events.PublishAsync(EventNames.SubtitlesUpdated, new { MovieId = movieId, SeriesId = seriesId }, ct);
            return Results.NoContent();
        });

        group.MapPost("/bulk/delete", async (BulkIdsRequest req, KrakarrDbContext db, IEventHub events, CancellationToken ct) =>
        {
            if (BulkLimits.Clean(req.Ids) is not { } ids) return Results.Problem("Select between 1 and " + BulkLimits.MaxIds + " subtitles.", statusCode: 400);
            return Results.Ok(await DeleteManyAsync(db, events, ids, ct));
        });

        return app;
    }

    /// <summary>Subtitle rows loaded and deleted per round trip of a bulk delete.</summary>
    private const int DeleteChunk = 200;

    /// <summary>Above this many affected titles, a bulk delete publishes one untargeted update instead of one per title.</summary>
    private const int MaxTargetedEvents = 20;

    /// <summary>Deletes external subtitle files and their rows, a chunk at a time; a file that can't be removed keeps its row.</summary>
    private static async Task<BulkResult> DeleteManyAsync(KrakarrDbContext db, IEventHub events, IReadOnlyList<int> ids, CancellationToken ct)
    {
        var (deleted, errors) = (0, new List<string>());
        var movieFileIds = new HashSet<int>();
        var episodeFileIds = new HashSet<int>();
        foreach (var chunk in ids.Chunk(DeleteChunk))
        {
            var rows = await db.SubtitleFiles.AsNoTracking().Where(s => chunk.Contains(s.Id))
                .Select(s => new { s.Id, s.Path, s.MovieFileId, s.EpisodeFileId }).ToListAsync(ct);
            var removed = new List<int>(rows.Count);
            foreach (var row in rows)
            {
                try
                {
                    if (File.Exists(row.Path)) File.Delete(row.Path);
                    removed.Add(row.Id);
                    if (row.MovieFileId is { } mf) movieFileIds.Add(mf);
                    if (row.EpisodeFileId is { } ef) episodeFileIds.Add(ef);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{Path.GetFileName(row.Path)}: {ex.Message}");
                }
            }

            if (removed.Count > 0) deleted += await db.SubtitleFiles.Where(s => removed.Contains(s.Id)).ExecuteDeleteAsync(ct);
        }

        if (deleted > 0)
        {
            // Detail pages match on the movie or series id; open lists reload on any update.
            var movieIds = new HashSet<int>();
            foreach (var chunk in movieFileIds.Chunk(DeleteChunk))
                movieIds.UnionWith(await db.MovieFiles.Where(f => chunk.Contains(f.Id)).Select(f => f.MovieId).Distinct().ToListAsync(ct));
            var seriesIds = new HashSet<int>();
            foreach (var chunk in episodeFileIds.Chunk(DeleteChunk))
                seriesIds.UnionWith(await db.EpisodeFiles.Where(f => chunk.Contains(f.Id)).Select(f => f.SeriesId).Distinct().ToListAsync(ct));

            if (movieIds.Count + seriesIds.Count > MaxTargetedEvents)
            {
                await events.PublishAsync(EventNames.SubtitlesUpdated, new { MovieId = (int?)null, SeriesId = (int?)null }, ct);
            }
            else
            {
                foreach (var movieId in movieIds)
                    await events.PublishAsync(EventNames.SubtitlesUpdated, new { MovieId = (int?)movieId, SeriesId = (int?)null }, ct);
                foreach (var seriesId in seriesIds)
                    await events.PublishAsync(EventNames.SubtitlesUpdated, new { MovieId = (int?)null, SeriesId = (int?)seriesId }, ct);
            }
        }

        return new BulkResult(deleted, errors);
    }

    private static Task<SubtitleTarget?> ResolveAsync(SubtitleService service, int? movieFileId, int? episodeFileId, CancellationToken ct) =>
        movieFileId is { } mf ? service.GetMovieTargetAsync(mf, ct)
        : episodeFileId is { } ef ? service.GetEpisodeTargetAsync(ef, ct)
        : Task.FromResult<SubtitleTarget?>(null);

    private static IEnumerable<SubtitleFileDto> EmbeddedDtos(int? movieFileId, int? episodeFileId, string relativePath, string? embedded, DateTimeOffset dateAdded) =>
        EmbeddedSubtitleList.Parse(embedded).Select(e => new SubtitleFileDto(
            0, movieFileId, episodeFileId, e.Language, Languages.Name(e.Language), e.HearingImpaired, e.Forced,
            Path.GetFileName(relativePath), EmbeddedProvider, null, null, dateAdded));

    /// <summary>Provider name of subtitle tracks inside the video; they have no row and cannot be deleted.</summary>
    public const string EmbeddedProvider = "embedded";

    private static SubtitleFileDto ToDto(SubtitleFile s) => new(
        s.Id, s.MovieFileId, s.EpisodeFileId, s.Language, Languages.Name(s.Language), s.HearingImpaired, s.Forced,
        Path.GetFileName(s.Path), s.Provider, s.Score, s.Release, s.DateAdded, s.Manual, s.SyncedAt, s.SyncNote);
}
