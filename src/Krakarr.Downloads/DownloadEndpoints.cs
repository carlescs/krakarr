using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Http;
using Krakarr.Data;
using Krakarr.Downloads.QBittorrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Krakarr.Downloads;

/// <param name="MovieId">Optional library movie to import the download into.</param>
/// <param name="SeriesId">Optional library series to import the download into.</param>
public sealed record AddMagnetRequest(string Magnet, string? Category, string? SavePath, bool Paused = false, int? MovieId = null, int? SeriesId = null);

public sealed record GrabReleaseRequest(ReleaseInfo Release, string? Category, int? MovieId = null, int? SeriesId = null);

public enum BulkDownloadAction { Pause, Resume, Remove }

/// <summary>A pause, resume or remove applied to every selected torrent (downloads page multi-selection).</summary>
public sealed record BulkDownloadRequest(IReadOnlyList<string> Hashes, BulkDownloadAction Action, bool DeleteData = false);

public static class DownloadEndpoints
{
    /// <summary>Most torrents one bulk request may name.</summary>
    private const int MaxBulkHashes = 5_000;

    public static IEndpointRouteBuilder MapDownloadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/downloads").WithTags("Downloads");

        group.MapGet("/", (ITorrentEngine engine) => engine.GetStatuses());

        group.MapPost("/magnet", (AddMagnetRequest req, ITorrentEngine engine, ManualDownloadLinks links, CancellationToken ct) =>
            AddAsync(() => links.AddAsync(
                new ManualDownloadLink(req.MovieId, req.SeriesId), req.Category,
                new AddTorrentOptions { SavePath = req.SavePath, Paused = req.Paused },
                o => engine.AddMagnetAsync(req.Magnet, o, ct), ct)));

        group.MapPost("/torrent", async (
            IFormFile file,
            [Microsoft.AspNetCore.Mvc.FromForm] string? category,
            [Microsoft.AspNetCore.Mvc.FromForm] int? movieId,
            [Microsoft.AspNetCore.Mvc.FromForm] int? seriesId,
            ITorrentEngine engine,
            ManualDownloadLinks links,
            CancellationToken ct) =>
        {
            if (file.Length is 0 or > BoundedContent.MaxTorrentBytes)
            {
                return Results.Problem("Torrent file must be between 1 byte and 10 MB", statusCode: 400);
            }

            // Read straight into an exactly sized array (a MemoryStream + ToArray would hold the file twice).
            var data = new byte[file.Length];
            await using (var upload = file.OpenReadStream())
            {
                await upload.ReadExactlyAsync(data, ct);
            }

            return await AddAsync(() => links.AddAsync(
                new ManualDownloadLink(movieId, seriesId), category, new AddTorrentOptions(),
                o => engine.AddTorrentAsync(data, o, ct), ct));
        }).DisableAntiforgery();

        group.MapPost("/release", (GrabReleaseRequest req, IDownloadService downloads, ManualDownloadLinks links, CancellationToken ct) =>
            AddAsync(() => links.AddAsync(
                new ManualDownloadLink(req.MovieId, req.SeriesId), req.Category, new AddTorrentOptions(),
                o => downloads.GrabAsync(req.Release, o, ct), ct)));

        group.MapPost("/{hash}/pause", (string hash, ITorrentEngine engine) => Control(() => engine.PauseAsync(hash)));
        group.MapPost("/{hash}/resume", (string hash, ITorrentEngine engine) => Control(() => engine.ResumeAsync(hash)));
        group.MapDelete("/{hash}", (string hash, bool? deleteData, ITorrentEngine engine) =>
            Control(() => engine.RemoveAsync(hash, deleteData ?? false)));

        group.MapPost("/bulk", async (BulkDownloadRequest req, ITorrentEngine engine) =>
        {
            if (req.Hashes is null || req.Hashes.Count is 0 or > MaxBulkHashes)
            {
                return Results.Problem($"Select between 1 and {MaxBulkHashes} torrents.", statusCode: 400);
            }

            if (!Enum.IsDefined(req.Action)) return Results.Problem("Unknown action.", statusCode: 400);

            // Torrents that are already gone (removed elsewhere) are skipped: they count as neither done nor failed.
            var names = engine.GetStatuses()
                .DistinctBy(t => t.InfoHash, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(t => t.InfoHash, t => t.Name, StringComparer.OrdinalIgnoreCase);
            var (affected, errors) = (0, new List<string>());
            foreach (var hash in req.Hashes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!names.TryGetValue(hash, out var name)) continue;
                try
                {
                    await (req.Action switch
                    {
                        BulkDownloadAction.Pause => engine.PauseAsync(hash),
                        BulkDownloadAction.Resume => engine.ResumeAsync(hash),
                        _ => engine.RemoveAsync(hash, req.DeleteData),
                    });
                    affected++;
                }
                catch (KeyNotFoundException)
                {
                    // Removed while the bulk action ran.
                }
                catch (Exception ex) when (ex is QBittorrentException or InvalidOperationException or HttpRequestException or IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{name}: {ex.Message}");
                }
            }

            return Results.Ok(new BulkResult(affected, errors));
        });

        group.MapGet("/{hash}/details", async (string hash, ITorrentEngine engine, CancellationToken ct) =>
        {
            try
            {
                return await engine.GetDetailsAsync(hash, ct) is { } details ? Results.Ok(details) : Results.NotFound();
            }
            catch (Exception ex) when (ex is QBittorrentException or InvalidOperationException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Which client new torrents go to and whether it is reachable, plus the other clients still running because
        // they own downloads.
        group.MapGet("/client", async (DownloadClientRouter router, ISettingsService settings, CancellationToken ct) =>
        {
            var s = await settings.GetAsync<DownloadClientSettings>(ct);
            var client = router.Get(s.Client);
            return new
            {
                Kind = s.Client,
                Connected = client.LastError is null,
                Error = client.LastError,
                Tag = s.Client == DownloadClientKind.QBittorrent ? s.QBittorrent.Tag : null,
                Others = router.All
                    .Where(c => c.Kind != s.Client && c.IsActive)
                    .Select(c => new { c.Kind, Connected = c.LastError is null, Error = c.LastError })
                    .ToList(),
            };
        });

        // Test a (possibly unsaved) configuration. A masked password or API key means "use the stored one".
        group.MapPost("/client/test", async (DownloadClientSettings input, DownloadClientRouter router, ISettingsService settings, CancellationToken ct) =>
        {
            if (input.QBittorrent.Password == QBittorrentSettings.PasswordMask || input.QBittorrent.ApiKey == QBittorrentSettings.PasswordMask)
            {
                var stored = await settings.GetAsync<DownloadClientSettings>(ct);
                input = input with { QBittorrent = input.QBittorrent.WithSecretsFrom(stored.QBittorrent) };
            }

            return await router.Get(input.Client).TestAsync(input, ct);
        });

        return app;
    }

    /// <summary>Adds a torrent and maps the expected failures to problem responses.</summary>
    private static async Task<IResult> AddAsync(Func<Task<Download>> add)
    {
        try
        {
            return Results.Ok(await add());
        }
        catch (KeyNotFoundException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> Control(Func<Task> action)
    {
        try
        {
            await action();
            return Results.NoContent();
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (Exception ex) when (ex is QBittorrentException or InvalidOperationException)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
