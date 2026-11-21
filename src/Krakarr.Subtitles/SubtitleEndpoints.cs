using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Jobs;
using Krakarr.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Subtitles;

public sealed record SubtitleFileDto(int Id, int? MovieFileId, int? EpisodeFileId, string Language, string LanguageName, bool HearingImpaired, bool Forced,
    string FileName, string Provider, int? Score, string? Release, DateTimeOffset DateAdded);

public sealed record SubtitleSearchRequest(int? MovieFileId, int? EpisodeFileId, int? MovieId = null, int? SeriesId = null);
public sealed record SubtitleDownloadRequest(int? MovieFileId, int? EpisodeFileId, SubtitleCandidate Candidate, int? Score);
public sealed record ScoredSubtitleDto(SubtitleCandidate Candidate, int Score, IReadOnlyList<string> Matches, IReadOnlyList<string> Rejections, bool Acceptable);
public sealed record WantedSubtitleDto(string MediaType, int? MovieId, int? SeriesId, int? MovieFileId, int? EpisodeFileId, string Label, IReadOnlyList<string> MissingLanguages);

public static class SubtitleEndpoints
{
    public static IEndpointRouteBuilder MapSubtitleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/subtitles").WithTags("Subtitles");

        group.MapGet("/languages", () => Languages.All.Select(l => new { l.Code, l.Name }));

        group.MapGet("/providers", async (SubtitleService service, ISettingsService settings, CancellationToken ct) =>
        {
            var s = await settings.GetAsync<SubtitleSettings>(ct);
            return service.Providers.Select(p => new { p.Name, Configured = p.IsConfigured(s) });
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

        // Subtitle files for a movie or a whole series.
        group.MapGet("/files", async (int? movieId, int? seriesId, KrakarrDbContext db, CancellationToken ct) =>
        {
            IQueryable<SubtitleFile> query;
            if (movieId is { } mid)
            {
                var fileIds = db.MovieFiles.Where(f => f.MovieId == mid).Select(f => f.Id);
                query = db.SubtitleFiles.Where(s => s.MovieFileId != null && fileIds.Contains(s.MovieFileId.Value));
            }
            else if (seriesId is { } sid)
            {
                var fileIds = db.EpisodeFiles.Where(f => f.SeriesId == sid).Select(f => f.Id);
                query = db.SubtitleFiles.Where(s => s.EpisodeFileId != null && fileIds.Contains(s.EpisodeFileId.Value));
            }
            else
            {
                return Results.Problem("movieId or seriesId is required", statusCode: 400);
            }

            var rows = await query.AsNoTracking().OrderBy(s => s.Language).ToListAsync(ct);
            return Results.Ok(rows.Select(ToDto));
        });

        group.MapGet("/wanted", async (int? limit, SubtitleService service, CancellationToken ct) =>
            (await service.WantedAsync(Math.Clamp(limit ?? 200, 1, 1000), ct)).Select(w => new WantedSubtitleDto(
                w.Target.MediaType.ToString(), w.Target.MovieId, w.Target.SeriesId, w.Target.MovieFileId, w.Target.EpisodeFileId, w.Target.Label, w.MissingLanguages)));

        // Automatic search for one file (every wanted language, upgrading existing subtitles), or all wanted files
        // (missing languages only) when no id is given.
        group.MapPost("/search", async (SubtitleSearchRequest req, SubtitleService service, KrakarrDbContext db, ICommandQueue commands, CancellationToken ct) =>
        {
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
                        if (await svc.GetEpisodeTargetAsync(id, token) is { } t) await svc.SearchMissingAsync(t, token, upgrade: true);
                        scopedDb.ChangeTracker.Clear();
                    }
                    return $"{title}: searched {ids.Count} episode file(s)";
                });
                return Results.Accepted(value: series);
            }

            if (req.MovieFileId is null && req.EpisodeFileId is null)
            {
                var command = commands.Enqueue("Search all missing subtitles", async (sp, token) =>
                {
                    var svc = sp.GetRequiredService<SubtitleService>();
                    var wanted = await svc.WantedAsync(100, token);
                    foreach (var w in wanted) await svc.SearchMissingAsync(w.Target, token);
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
                return await svc.SearchMissingAsync(t, token, upgrade: true);
            });
            return Results.Accepted(value: single);
        });

        // Interactive search: every candidate with its score and why it would be skipped automatically.
        group.MapGet("/candidates", async (int? movieFileId, int? episodeFileId, string? language, SubtitleService service, ISettingsService settings, CancellationToken ct) =>
        {
            var target = await ResolveAsync(service, movieFileId, episodeFileId, ct);
            if (target is null) return Results.NotFound();
            var s = await settings.GetAsync<SubtitleSettings>(ct);
            var languages = language is not null ? [Languages.Normalize(language) ?? language] : s.Languages;
            var (candidates, errors) = await service.SearchProvidersAsync(target, languages, s, ct);
            var ranked = languages.SelectMany(l => SubtitleScorer.Rank(target.Video, candidates.Where(c => c.Language == l), s))
                .OrderByDescending(r => r.Score)
                .Select(r => new ScoredSubtitleDto(r.Candidate, r.Score, r.Matches, r.Rejections, r.Acceptable));
            return Results.Ok(new { target.Label, Results = ranked, Errors = errors });
        });

        group.MapPost("/download", async (SubtitleDownloadRequest req, SubtitleService service, CancellationToken ct) =>
        {
            var target = await ResolveAsync(service, req.MovieFileId, req.EpisodeFileId, ct);
            if (target is null) return Results.NotFound();
            try
            {
                return Results.Ok(ToDto(await service.DownloadAsync(target, req.Candidate, req.Score, ct: ct)));
            }
            catch (SubtitleProviderException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        group.MapDelete("/{id:int}", async (int id, KrakarrDbContext db, CancellationToken ct) =>
        {
            var row = await db.SubtitleFiles.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (row is null) return Results.NotFound();
            if (File.Exists(row.Path)) File.Delete(row.Path);
            db.SubtitleFiles.Remove(row);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return app;
    }

    private static Task<SubtitleTarget?> ResolveAsync(SubtitleService service, int? movieFileId, int? episodeFileId, CancellationToken ct) =>
        movieFileId is { } mf ? service.GetMovieTargetAsync(mf, ct)
        : episodeFileId is { } ef ? service.GetEpisodeTargetAsync(ef, ct)
        : Task.FromResult<SubtitleTarget?>(null);

    private static SubtitleFileDto ToDto(SubtitleFile s) => new(
        s.Id, s.MovieFileId, s.EpisodeFileId, s.Language, Languages.Name(s.Language), s.HearingImpaired, s.Forced,
        Path.GetFileName(s.Path), s.Provider, s.Score, s.Release, s.DateAdded);
}
