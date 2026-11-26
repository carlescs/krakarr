using System.Reflection;
using System.Runtime.InteropServices;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Jobs;
using Krakarr.Data;
using Krakarr.Host.Auth;
using Microsoft.Extensions.Options;

namespace Krakarr.Host.Endpoints;

public static class SystemEndpoints
{
    public static readonly string Version =
        typeof(SystemEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/health", () => Results.Ok(new { Status = "ok", Version })).AllowAnonymous().WithTags("System");

        var group = app.MapGroup("/api/v1/system").WithTags("System");
        group.MapGet("/status", (KrakarrPaths paths, IOptions<DatabaseOptions> db, IWebHostEnvironment env) => new
        {
            Version,
            Runtime = RuntimeInformation.FrameworkDescription,
            Os = RuntimeInformation.OSDescription,
            IsDocker = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true",
            ConfigDirectory = paths.ConfigDirectory,
            DownloadDirectory = paths.DefaultDownloadDirectory,
            DatabaseProvider = db.Value.Provider,
            env.EnvironmentName,
            StartTime = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
        });

        group.MapGet("/commands", (ICommandQueue commands) => commands.Recent);

        // Long-running work (e.g. subtitle syncs) for the tasks menu in the top bar: waiting, running and recently finished.
        // Open to every user (RoleAccess): each sees the tasks they started, and staff also see automatic ones.
        group.MapGet("/background-tasks", (HttpContext http, BackgroundTasks tasks) => tasks.For(Viewer(http)));
        group.MapPost("/background-tasks/{id}/cancel", (string id, HttpContext http, BackgroundTasks tasks) =>
            tasks.Cancel(id, Viewer(http)) ? Results.NoContent() : Results.NotFound());
        group.MapDelete("/background-tasks/{id}", (string id, HttpContext http, BackgroundTasks tasks) =>
            tasks.Dismiss(id, Viewer(http)) ? Results.NoContent() : Results.NotFound());
        group.MapDelete("/background-tasks", (HttpContext http, BackgroundTasks tasks) =>
        {
            tasks.DismissFinished(Viewer(http));
            return Results.NoContent();
        });

        group.MapGet("/tasks", (IScheduledTaskManager tasks) => tasks.GetTasks());
        group.MapPost("/tasks/{id}/run", (string id, IScheduledTaskManager tasks) => tasks.Trigger(id) switch
        {
            TaskTriggerResult.Started => Results.Accepted(),
            TaskTriggerResult.AlreadyRunning => Results.Conflict(new { Message = "The task is already running" }),
            _ => Results.NotFound(),
        });

        return app;
    }

    private static BackgroundTaskViewer Viewer(HttpContext http) =>
        new(http.User.GetUserId(), http.User.GetRole() is UserRole.Admin or UserRole.Manager);
}
