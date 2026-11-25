using Krakarr.Core.Jobs;
using Microsoft.AspNetCore.Http.Features;

namespace Krakarr.Host.Backups;

public static class BackupEndpoints
{
    public static IServiceCollection AddKrakarrBackups(this IServiceCollection services, RestoreOutcome? restore)
    {
        services.AddScoped<BackupService>();
        services.AddScheduledTask<BackupTask>();
        services.AddSingleton(new RestoreStatus(restore));
        return services;
    }

    /// <summary>Admin only (see <c>RoleAccess</c>): the archives hold every secret Krakarr has.</summary>
    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/system/backups").WithTags("Backups");

        group.MapGet("/", (BackupService backups, RestoreStatus status) => new
        {
            Backups = backups.List(),
            backups.RestorePending,
            LastRestore = status.Last,
            backups.Provider,
        });

        group.MapPost("/", async (BackupService backups, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await backups.CreateAsync(BackupType.Manual, wait: false, ct));
            }
            catch (BackupInProgressException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        group.MapGet("/{name}", (string name, BackupService backups) =>
            backups.PathOf(name) is { } path
                ? Results.File(path, "application/zip", name, enableRangeProcessing: true)
                : Results.NotFound());

        group.MapDelete("/{name}", (string name, BackupService backups) => backups.Delete(name) ? Results.NoContent() : Results.NotFound());

        // Upload a backup (raw zip body) to restore on the next start. Streamed to disk, never buffered in memory.
        group.MapPost("/restore", async (HttpContext http, BackupService backups, CancellationToken ct) =>
        {
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = BackupService.MaxUploadBytes;
            return await backups.StageRestoreAsync(http.Request.Body, ct) is { } error
                ? Results.Problem(error, statusCode: StatusCodes.Status400BadRequest)
                : Results.Accepted();
        });

        group.MapPost("/{name}/restore", async (string name, BackupService backups, CancellationToken ct) =>
        {
            if (backups.PathOf(name) is null) return Results.NotFound();
            return await backups.StageRestoreAsync(name, ct) is { } error
                ? Results.Problem(error, statusCode: StatusCodes.Status400BadRequest)
                : Results.Accepted();
        });

        group.MapDelete("/restore", (BackupService backups) => backups.CancelRestore() ? Results.NoContent() : Results.NotFound());

        return app;
    }
}
