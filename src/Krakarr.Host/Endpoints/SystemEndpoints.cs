using System.Reflection;
using System.Runtime.InteropServices;
using Krakarr.Core.Configuration;
using Krakarr.Core.Jobs;
using Krakarr.Data;
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
        group.MapGet("/background-tasks", (BackgroundTasks tasks) => tasks.Current);
        group.MapPost("/background-tasks/{id}/cancel", (string id, BackgroundTasks tasks) =>
            tasks.Cancel(id) ? Results.NoContent() : Results.NotFound());
        group.MapDelete("/background-tasks/{id}", (string id, BackgroundTasks tasks) =>
            tasks.Dismiss(id) ? Results.NoContent() : Results.NotFound());
        group.MapDelete("/background-tasks", (BackgroundTasks tasks) =>
        {
            tasks.DismissFinished();
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
}
