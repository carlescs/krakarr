using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Core.Jobs;
using Krakarr.Core.Metadata;
using Krakarr.Data;
using Krakarr.Host.Auth;
using Krakarr.Movies;
using Krakarr.Series;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Endpoints;

public sealed record CreateMediaRequest(MediaType MediaType, int TmdbId);

/// <summary>
/// Library options chosen when approving. Movies use <see cref="Monitored"/>, <see cref="MinimumAvailability"/> and
/// <see cref="SearchNow"/>; series use <see cref="Monitor"/>, <see cref="SeasonFolder"/> and <see cref="SearchNow"/>.
/// </summary>
public sealed record ApproveMediaRequest(
    int QualityProfileId,
    string RootFolderPath,
    bool Monitored = true,
    MinimumAvailability MinimumAvailability = MinimumAvailability.Released,
    MonitorMode Monitor = MonitorMode.All,
    bool SeasonFolder = true,
    bool SearchNow = true);

public sealed record DeclineMediaRequest(string? Reason);

/// <summary>How much of a requested title is in the library: Missing (added, nothing downloaded yet), Partial or Available.</summary>
public enum RequestAvailability { Missing, Partial, Available }

public sealed record MediaRequestDto(
    int Id,
    MediaType MediaType,
    int TmdbId,
    int? TvdbId,
    string Title,
    int Year,
    string? PosterUrl,
    RequestStatus Status,
    int RequestedById,
    string? RequestedBy,
    DateTimeOffset RequestedAt,
    string? ProcessedBy,
    DateTimeOffset? ProcessedAt,
    string? DeclineReason,
    /// <summary>Library movie or series id, when the title is in the library.</summary>
    int? LibraryId,
    /// <summary>Null when the title is not in the library.</summary>
    RequestAvailability? Availability);

public sealed record MediaRequestPage(IReadOnlyList<MediaRequestDto> Results, int Page, int TotalPages, int Total);

/// <summary>
/// Seerr-style requests. Every signed-in user may request titles that are not in the library and see or withdraw
/// their own pending requests; admins and managers see everyone's and approve (adding the title) or decline them.
/// <see cref="RoleAccess"/> keeps approve/decline to staff; ownership is checked here.
/// </summary>
public static class RequestEndpoints
{
    public const int PageSize = 50;

    /// <summary>Caps how many open requests one user can pile up.</summary>
    public const int MaxPendingPerUser = 100;

    public static IEndpointRouteBuilder MapRequestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/requests").WithTags("Requests");

        group.MapGet("/", async (string? status, int? page, HttpContext http, KrakarrDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var query = Visible(db.MediaRequests.AsNoTracking(), http);
            if (!string.IsNullOrEmpty(status))
            {
                if (!Enum.TryParse<RequestStatus>(status, ignoreCase: true, out var s) || !Enum.IsDefined(s))
                    return Results.Problem($"Unknown status '{status}'", statusCode: StatusCodes.Status400BadRequest);
                query = query.Where(r => r.Status == s);
            }

            var total = await query.CountAsync(ct);
            var totalPages = Math.Max(1, (total + PageSize - 1) / PageSize);
            var current = Math.Clamp(page ?? 1, 1, totalPages);
            var rows = await Project(query.OrderByDescending(r => r.RequestedAt).ThenByDescending(r => r.Id)
                .Skip((current - 1) * PageSize).Take(PageSize)).ToListAsync(ct);
            return Results.Ok(new MediaRequestPage(await WithAvailabilityAsync(db, rows, Today(time), ct), current, totalPages, total));
        });

        // The open request for a title, if any (whoever made it), so detail pages can show "Requested".
        group.MapGet("/tmdb/{mediaType}/{tmdbId:int}", async (MediaType mediaType, int tmdbId, KrakarrDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var rows = await Project(db.MediaRequests.AsNoTracking()
                .Where(r => r.MediaType == mediaType && r.TmdbId == tmdbId && r.Status == RequestStatus.Pending)
                .OrderByDescending(r => r.RequestedAt).Take(1)).ToListAsync(ct);
            return rows.Count == 0 ? Results.NoContent() : Results.Ok((await WithAvailabilityAsync(db, rows, Today(time), ct))[0]);
        });

        group.MapPost("/", async (CreateMediaRequest req, HttpContext http, KrakarrDbContext db, ITmdbClient tmdb, ISeriesDiscovery seriesDiscovery,
            IEventHub events, TimeProvider time, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } userId)
                return Results.Problem("Requests are made by user accounts, not the API key.", statusCode: StatusCodes.Status400BadRequest);
            if (!Enum.IsDefined(req.MediaType) || req.TmdbId <= 0)
                return Results.Problem("A movie or series TMDB id is required.", statusCode: StatusCodes.Status400BadRequest);

            if (await PendingAsync(db, req.MediaType, req.TmdbId, ct) is { } existing)
                return Results.Problem($"{existing.Title} has already been requested.", statusCode: StatusCodes.Status409Conflict);
            if (await InLibraryAsync(db, req.MediaType, req.TmdbId, null, ct))
                return Results.Problem("This title is already in the library.", statusCode: StatusCodes.Status409Conflict);
            if (await db.MediaRequests.CountAsync(r => r.RequestedById == userId && r.Status == RequestStatus.Pending, ct) >= MaxPendingPerUser)
                return Results.Problem($"You already have {MaxPendingPerUser} open requests. Wait for some to be handled first.", statusCode: StatusCodes.Status409Conflict);

            var request = new MediaRequest { MediaType = req.MediaType, TmdbId = req.TmdbId, Title = string.Empty, RequestedById = userId, RequestedAt = time.GetUtcNow() };
            try
            {
                if (req.MediaType == MediaType.Movie)
                {
                    var movie = await tmdb.GetMovieAsync(req.TmdbId, ct);
                    (request.Title, request.Year, request.PosterUrl) = (movie.Title, movie.Year, movie.PosterUrl);
                }
                else
                {
                    var show = (await seriesDiscovery.DetailsAsync(req.TmdbId, ct)).Show;
                    (request.Title, request.Year, request.PosterUrl, request.TvdbId) = (show.Title, show.Year, show.PosterUrl, show.TvdbId);
                    if (show.TvdbId is not null && await InLibraryAsync(db, req.MediaType, req.TmdbId, show.TvdbId, ct))
                        return Results.Problem("This title is already in the library.", statusCode: StatusCodes.Status409Conflict);
                }
            }
            catch (MetadataException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }

            request.Title = Truncate(request.Title, 500)!;
            request.PosterUrl = Truncate(request.PosterUrl, 500);
            db.MediaRequests.Add(request);
            await db.SaveChangesAsync(ct);
            await events.PublishAsync(EventNames.RequestsUpdated, null, ct);
            var dto = await DtoAsync(db, request.Id, Today(time), ct);
            return Results.Created($"/api/v1/requests/{request.Id}", dto);
        });

        // The caller has seen how their requests were handled (the Requests page calls this), so the bell stops
        // reporting approvals and declines.
        group.MapPost("/seen", async (HttpContext http, KrakarrDbContext db, IEventHub events, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } userId) return Results.NoContent();
            var changed = await db.MediaRequests.Where(r => r.RequestedById == userId && r.ResultUnseen)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ResultUnseen, false), ct);
            if (changed > 0) await events.PublishAsync(EventNames.NotificationsUpdated, null, ct);
            return Results.NoContent();
        });

        // Users withdraw their own pending requests; staff remove any request.
        group.MapDelete("/{id:int}", async (int id, HttpContext http, KrakarrDbContext db, IEventHub events, CancellationToken ct) =>
        {
            var request = await db.MediaRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (request is null) return Results.NotFound();
            if (!IsStaff(http))
            {
                if (request.RequestedById != http.User.GetUserId()) return Results.NotFound();
                if (request.Status != RequestStatus.Pending)
                    return Results.Problem("Only pending requests can be withdrawn.", statusCode: StatusCodes.Status409Conflict);
            }

            db.MediaRequests.Remove(request);
            await db.SaveChangesAsync(ct);
            await events.PublishAsync(EventNames.RequestsUpdated, null, ct);
            return Results.NoContent();
        });

        // Staff only (RoleAccess): adds the title with the chosen options, or just marks the request approved when
        // the title is in the library already.
        group.MapPost("/{id:int}/approve", async (int id, ApproveMediaRequest req, HttpContext http, KrakarrDbContext db, MovieService movies,
            SeriesService series, ICommandQueue commands, IEventHub events, TimeProvider time, CancellationToken ct) =>
        {
            var request = await db.MediaRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (request is null) return Results.NotFound();
            if (request.Status == RequestStatus.Approved)
                return Results.Problem("This request has already been approved.", statusCode: StatusCodes.Status409Conflict);

            if (!await InLibraryAsync(db, request.MediaType, request.TmdbId, request.TvdbId, ct))
            {
                try
                {
                    if (request.MediaType == MediaType.Movie)
                    {
                        var movie = await movies.AddAsync(new AddMovieRequest(request.TmdbId, req.QualityProfileId, req.RootFolderPath,
                            req.Monitored, req.SearchNow, MinimumAvailability: req.MinimumAvailability), ct);
                        if (req.SearchNow && movie.Monitored)
                        {
                            var movieId = movie.Id;
                            commands.Enqueue($"Search: {movie.Title}", async (sp, token) => await sp.GetRequiredService<MovieService>().AutoSearchAsync(movieId, token));
                        }
                    }
                    else
                    {
                        var show = await series.AddAsync(new AddSeriesRequest(request.TmdbId, request.TvdbId, req.QualityProfileId, req.RootFolderPath,
                            req.Monitor, req.SeasonFolder, req.SearchNow), ct);
                        if (req.SearchNow && req.Monitor != MonitorMode.None)
                        {
                            var seriesId = show.Id;
                            commands.Enqueue($"Search: {show.Title}", async (sp, token) => await sp.GetRequiredService<SeriesService>().AutoSearchAsync(seriesId, null, null, token));
                        }
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or MetadataException)
                {
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
                }
            }

            // AddAsync already approved matching pending requests and subscribed their requesters; cover a title that
            // was in the library already, then record who approved this one.
            if (await LibraryIdAsync(db, request.MediaType, request.TmdbId, request.TvdbId, ct) is { } libraryId)
                await MediaSubscriptions.SubscribeAsync(db, [request.RequestedById], request.MediaType, libraryId, ct);
            request.Status = RequestStatus.Approved;
            request.ProcessedById = http.User.GetUserId();
            request.ProcessedAt = time.GetUtcNow();
            request.DeclineReason = null;
            request.ResultUnseen = request.RequestedById != request.ProcessedById;
            await db.SaveChangesAsync(ct);
            await events.PublishAsync(EventNames.RequestsUpdated, null, ct);
            return Results.Ok(await DtoAsync(db, request.Id, Today(time), ct));
        });

        group.MapPost("/{id:int}/decline", async (int id, DeclineMediaRequest? req, HttpContext http, KrakarrDbContext db, IEventHub events, TimeProvider time, CancellationToken ct) =>
        {
            var request = await db.MediaRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (request is null) return Results.NotFound();
            if (request.Status != RequestStatus.Pending)
                return Results.Problem("Only pending requests can be declined.", statusCode: StatusCodes.Status409Conflict);

            request.Status = RequestStatus.Declined;
            request.ProcessedById = http.User.GetUserId();
            request.ProcessedAt = time.GetUtcNow();
            request.DeclineReason = Truncate(string.IsNullOrWhiteSpace(req?.Reason) ? null : req.Reason.Trim(), 500);
            request.ResultUnseen = request.RequestedById != request.ProcessedById;
            await db.SaveChangesAsync(ct);
            await events.PublishAsync(EventNames.RequestsUpdated, null, ct);
            return Results.Ok(await DtoAsync(db, request.Id, Today(time), ct));
        });

        return app;
    }

    private static bool IsStaff(HttpContext http) => http.User.GetRole() is UserRole.Admin or UserRole.Manager;

    /// <summary>Staff see every request; plain users only their own.</summary>
    private static IQueryable<MediaRequest> Visible(IQueryable<MediaRequest> query, HttpContext http)
    {
        if (IsStaff(http)) return query;
        var userId = http.User.GetUserId() ?? -1;
        return query.Where(r => r.RequestedById == userId);
    }

    private static Task<MediaRequest?> PendingAsync(KrakarrDbContext db, MediaType type, int tmdbId, CancellationToken ct) =>
        db.MediaRequests.AsNoTracking().FirstOrDefaultAsync(r => r.MediaType == type && r.TmdbId == tmdbId && r.Status == RequestStatus.Pending, ct);

    private static Task<bool> InLibraryAsync(KrakarrDbContext db, MediaType type, int tmdbId, int? tvdbId, CancellationToken ct) =>
        type == MediaType.Movie
            ? db.Movies.AnyAsync(m => m.TmdbId == tmdbId, ct)
            : db.Series.AnyAsync(s => s.TmdbId == tmdbId || (tvdbId != null && s.TvdbId == tvdbId), ct);

    private static Task<int?> LibraryIdAsync(KrakarrDbContext db, MediaType type, int tmdbId, int? tvdbId, CancellationToken ct) =>
        type == MediaType.Movie
            ? db.Movies.Where(m => m.TmdbId == tmdbId).Select(m => (int?)m.Id).FirstOrDefaultAsync(ct)
            : db.Series.Where(s => s.TmdbId == tmdbId || (tvdbId != null && s.TvdbId == tvdbId)).Select(s => (int?)s.Id).FirstOrDefaultAsync(ct);

    private static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    private static string? Truncate(string? value, int max) => value is { Length: var n } && n > max ? value[..max] : value;

    private static IQueryable<MediaRequestDto> Project(IQueryable<MediaRequest> query) =>
        query.Select(r => new MediaRequestDto(r.Id, r.MediaType, r.TmdbId, r.TvdbId, r.Title, r.Year, r.PosterUrl, r.Status, r.RequestedById,
            r.RequestedBy!.Username, r.RequestedAt, r.ProcessedBy!.Username, r.ProcessedAt, r.DeclineReason, null, null));

    private static async Task<MediaRequestDto> DtoAsync(KrakarrDbContext db, int id, DateOnly today, CancellationToken ct)
    {
        var rows = await Project(db.MediaRequests.AsNoTracking().Where(r => r.Id == id)).ToListAsync(ct);
        return (await WithAvailabilityAsync(db, rows, today, ct))[0];
    }

    /// <summary>Links each request to its library title and how much of it is downloaded, with one query per media type.</summary>
    private static async Task<IReadOnlyList<MediaRequestDto>> WithAvailabilityAsync(KrakarrDbContext db, IReadOnlyList<MediaRequestDto> rows, DateOnly today, CancellationToken ct)
    {
        if (rows.Count == 0) return rows;

        var movieIds = rows.Where(r => r.MediaType == MediaType.Movie).Select(r => r.TmdbId).Distinct().ToList();
        var movies = movieIds.Count == 0
            ? []
            : await db.Movies.AsNoTracking().Where(m => movieIds.Contains(m.TmdbId))
                .Select(m => new { m.TmdbId, m.Id, HasFile = m.MovieFileId != null })
                .ToDictionaryAsync(m => m.TmdbId, ct);

        var showTmdb = rows.Where(r => r.MediaType == MediaType.Series).Select(r => (int?)r.TmdbId).Distinct().ToList();
        var showTvdb = rows.Where(r => r.MediaType == MediaType.Series && r.TvdbId != null).Select(r => r.TvdbId).Distinct().ToList();
        var shows = showTmdb.Count == 0
            ? []
            : await db.Series.AsNoTracking().Where(s => showTmdb.Contains(s.TmdbId) || showTvdb.Contains(s.TvdbId))
                .Select(s => new
                {
                    s.Id,
                    s.TmdbId,
                    s.TvdbId,
                    Aired = db.Episodes.Count(e => e.SeriesId == s.Id && e.SeasonNumber > 0 && e.AirDate != null && e.AirDate <= today),
                    WithFile = db.Episodes.Count(e => e.SeriesId == s.Id && e.SeasonNumber > 0 && e.AirDate != null && e.AirDate <= today && e.EpisodeFileId != null),
                })
                .ToListAsync(ct);

        return rows.Select(r =>
        {
            if (r.MediaType == MediaType.Movie)
            {
                return movies.TryGetValue(r.TmdbId, out var m)
                    ? r with { LibraryId = m.Id, Availability = m.HasFile ? RequestAvailability.Available : RequestAvailability.Missing }
                    : r;
            }

            var s = shows.FirstOrDefault(x => x.TmdbId == r.TmdbId) ?? shows.FirstOrDefault(x => r.TvdbId != null && x.TvdbId == r.TvdbId);
            if (s is null) return r;
            var availability = s.WithFile == 0 ? RequestAvailability.Missing
                : s.WithFile >= s.Aired ? RequestAvailability.Available
                : RequestAvailability.Partial;
            return r with { LibraryId = s.Id, Availability = availability };
        }).ToList();
    }
}
