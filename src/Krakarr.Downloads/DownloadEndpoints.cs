using Krakarr.Core.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Krakarr.Downloads;

public sealed record AddMagnetRequest(string Magnet, string? Category, string? SavePath, bool Paused = false);

public sealed record GrabReleaseRequest(ReleaseInfo Release, string? Category);

public static class DownloadEndpoints
{
    public static IEndpointRouteBuilder MapDownloadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/downloads").WithTags("Downloads");

        group.MapGet("/", (ITorrentEngine engine) => engine.GetStatuses());

        group.MapPost("/magnet", async (AddMagnetRequest req, ITorrentEngine engine, CancellationToken ct) =>
        {
            try
            {
                var row = await engine.AddMagnetAsync(req.Magnet, new AddTorrentOptions
                {
                    Category = req.Category ?? "default",
                    SavePath = req.SavePath,
                    Paused = req.Paused,
                }, ct);
                return Results.Ok(row);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapPost("/torrent", async (IFormFile file, [Microsoft.AspNetCore.Mvc.FromForm] string? category, ITorrentEngine engine, CancellationToken ct) =>
        {
            if (file.Length is 0 or > 10 * 1024 * 1024)
            {
                return Results.Problem("Torrent file must be between 1 byte and 10 MB", statusCode: 400);
            }

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            try
            {
                var row = await engine.AddTorrentAsync(ms.ToArray(), new AddTorrentOptions { Category = category ?? "default" }, ct);
                return Results.Ok(row);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        }).DisableAntiforgery();

        group.MapPost("/release", async (GrabReleaseRequest req, IDownloadService downloads, CancellationToken ct) =>
        {
            try
            {
                var row = await downloads.GrabAsync(req.Release, new AddTorrentOptions { Category = req.Category ?? "default" }, ct);
                return Results.Ok(row);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapPost("/{hash}/pause", async (string hash, ITorrentEngine engine) =>
        {
            try { await engine.PauseAsync(hash); return Results.NoContent(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        group.MapPost("/{hash}/resume", async (string hash, ITorrentEngine engine) =>
        {
            try { await engine.ResumeAsync(hash); return Results.NoContent(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        group.MapDelete("/{hash}", async (string hash, bool? deleteData, ITorrentEngine engine) =>
        {
            await engine.RemoveAsync(hash, deleteData ?? false);
            return Results.NoContent();
        });

        return app;
    }
}
