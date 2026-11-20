using System.Collections.Concurrent;
using System.Threading.Channels;
using Krakarr.Core.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Krakarr.Core.Jobs;

/// <summary>A task that runs periodically inside its own DI scope. It can also be run on demand from the UI.</summary>
public interface IScheduledTask
{
    string Name { get; }

    /// <summary>Default interval between scheduled runs.</summary>
    TimeSpan Interval { get; }

    TimeSpan InitialDelay => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Interval to use right now, for tasks whose schedule comes from settings. <c>null</c> disables scheduled
    /// runs; the task can still be run manually. Re-read at least once a minute, so settings changes apply quickly.
    /// </summary>
    ValueTask<TimeSpan?> GetIntervalAsync(CancellationToken cancellationToken) => ValueTask.FromResult<TimeSpan?>(Interval);

    Task ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>State of a scheduled task, as shown in the UI.</summary>
public sealed record ScheduledTaskInfo(
    string Id,
    string Name,
    double? IntervalSeconds,
    DateTimeOffset? LastRun,
    double? LastDurationSeconds,
    bool? LastSucceeded,
    string? LastError,
    DateTimeOffset? NextRun,
    bool IsRunning);

public enum TaskTriggerResult
{
    Started,
    NotFound,
    AlreadyRunning,
}

/// <summary>Lists scheduled tasks and runs them on demand.</summary>
public interface IScheduledTaskManager
{
    IReadOnlyList<ScheduledTaskInfo> GetTasks();

    /// <summary>Runs the task now (in its own loop, so it never overlaps a scheduled run) and reschedules it from now.</summary>
    TaskTriggerResult Trigger(string id);
}

public sealed class ScheduledTaskRunner : BackgroundService, IScheduledTaskManager
{
    /// <summary>How often a waiting task re-reads its interval, so settings changes apply without a restart.</summary>
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventHub _eventHub;
    private readonly ILogger<ScheduledTaskRunner> _logger;
    private readonly TaskState[] _tasks;

    public ScheduledTaskRunner(
        IServiceScopeFactory scopeFactory,
        IEnumerable<ScheduledTaskRegistration> registrations,
        IEventHub eventHub,
        ILogger<ScheduledTaskRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _eventHub = eventHub;
        _logger = logger;
        using var scope = scopeFactory.CreateScope();
        var now = DateTimeOffset.UtcNow;
        _tasks = registrations.Select(r =>
        {
            var task = (IScheduledTask)scope.ServiceProvider.GetRequiredService(r.TaskType);
            return new TaskState(r.Id, r.TaskType, task.Name)
            {
                Interval = task.Interval,
                FirstRun = now + task.InitialDelay,
                NextRun = now + task.InitialDelay,
            };
        }).ToArray();
    }

    public IReadOnlyList<ScheduledTaskInfo> GetTasks() => _tasks.Select(t => t.ToInfo()).ToList();

    public TaskTriggerResult Trigger(string id)
    {
        var task = Array.Find(_tasks, t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        if (task is null) return TaskTriggerResult.NotFound;
        lock (task)
        {
            if (task.IsRunning || task.TriggerPending) return TaskTriggerResult.AlreadyRunning;
            task.TriggerPending = true;
        }

        task.Signal.Release();
        return TaskTriggerResult.Started;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(_tasks.Select(t => RunLoopAsync(t, stoppingToken)));

    private async Task RunLoopAsync(TaskState state, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var interval = await GetIntervalAsync(state, ct);
            var due = state.LastRun is { } last ? last + interval : interval is null ? null : state.FirstRun;
            state.Interval = interval;
            state.NextRun = due;

            var now = DateTimeOffset.UtcNow;
            if (due is null || due > now)
            {
                var wait = due is null ? RecheckInterval : Min(due.Value - now, RecheckInterval);
                try
                {
                    await state.Signal.WaitAsync(wait, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                bool manual;
                lock (state)
                {
                    manual = state.TriggerPending;
                }

                if (!manual && (due is null || DateTimeOffset.UtcNow < due)) continue;
            }

            if (!await RunAsync(state, ct)) return;
        }
    }

    private async Task<TimeSpan?> GetIntervalAsync(TaskState state, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var task = (IScheduledTask)scope.ServiceProvider.GetRequiredService(state.TaskType);
        try
        {
            return await task.GetIntervalAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the interval of {Task}", state.Name);
            return task.Interval;
        }
    }

    /// <summary>Runs the task once. Returns false when the host is stopping.</summary>
    private async Task<bool> RunAsync(TaskState state, CancellationToken ct)
    {
        lock (state)
        {
            state.IsRunning = true;
            state.TriggerPending = false;
        }

        var started = DateTimeOffset.UtcNow;
        state.LastRun = started;
        state.NextRun = state.Interval is { } i ? started + i : null;
        await PublishAsync(state);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await ((IScheduledTask)scope.ServiceProvider.GetRequiredService(state.TaskType)).ExecuteAsync(ct);
            state.LastSucceeded = true;
            state.LastError = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled task {Task} failed", state.Name);
            state.LastSucceeded = false;
            state.LastError = ex.Message;
        }
        finally
        {
            state.LastDuration = DateTimeOffset.UtcNow - started;
            lock (state)
            {
                state.IsRunning = false;
            }

            // A trigger can't arrive while running, but drain a stray signal so the next wait isn't cut short.
            while (state.Signal.CurrentCount > 0 && state.Signal.Wait(0))
            {
            }
        }

        await PublishAsync(state);
        return true;
    }

    private async Task PublishAsync(TaskState state)
    {
        try
        {
            await _eventHub.PublishAsync(EventNames.TaskUpdated, state.ToInfo());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not publish the state of {Task}", state.Name);
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private sealed class TaskState(string id, Type taskType, string name)
    {
        public string Id { get; } = id;
        public Type TaskType { get; } = taskType;
        public string Name { get; } = name;
        public SemaphoreSlim Signal { get; } = new(0);
        public bool IsRunning { get; set; }
        public bool TriggerPending { get; set; }
        public TimeSpan? Interval { get; set; }
        public DateTimeOffset FirstRun { get; init; }
        public DateTimeOffset? LastRun { get; set; }
        public TimeSpan? LastDuration { get; set; }
        public bool? LastSucceeded { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset? NextRun { get; set; }

        public ScheduledTaskInfo ToInfo() => new(
            Id, Name, Interval?.TotalSeconds, LastRun, LastDuration?.TotalSeconds, LastSucceeded, LastError, NextRun, IsRunning);
    }
}

/// <param name="TaskType">The <see cref="IScheduledTask"/> implementation, registered as scoped.</param>
/// <param name="Id">Stable id used by the API, e.g. <c>MovieRefresh</c>.</param>
public sealed record ScheduledTaskRegistration(Type TaskType, string Id)
{
    public ScheduledTaskRegistration(Type taskType)
        : this(taskType, DefaultId(taskType))
    {
    }

    /// <summary>Type name without a trailing <c>Task</c> or <c>Service</c>: <c>MovieRefreshTask</c> → <c>MovieRefresh</c>.</summary>
    private static string DefaultId(Type type)
    {
        foreach (var suffix in (string[])["Task", "Service"])
        {
            if (type.Name.Length > suffix.Length && type.Name.EndsWith(suffix, StringComparison.Ordinal)) return type.Name[..^suffix.Length];
        }

        return type.Name;
    }
}

public enum CommandStatus
{
    Queued,
    Running,
    Completed,
    Failed,
}

public sealed class CommandInfo
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public CommandStatus Status { get; set; } = CommandStatus.Queued;
    public string? Message { get; set; }
    public DateTimeOffset Queued { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Started { get; set; }
    public DateTimeOffset? Ended { get; set; }
}

/// <summary>Queue for user-triggered background commands (e.g. "search movie").</summary>
public interface ICommandQueue
{
    CommandInfo Enqueue(string name, Func<IServiceProvider, CancellationToken, Task<string?>> work);
    IReadOnlyList<CommandInfo> Recent { get; }
}

public sealed class CommandQueue(
    IServiceScopeFactory scopeFactory,
    IEventHub eventHub,
    ILogger<CommandQueue> logger) : BackgroundService, ICommandQueue
{
    private readonly Channel<(CommandInfo Info, Func<IServiceProvider, CancellationToken, Task<string?>> Work)> _channel =
        Channel.CreateUnbounded<(CommandInfo, Func<IServiceProvider, CancellationToken, Task<string?>>)>();

    private readonly ConcurrentQueue<CommandInfo> _recent = new();

    public IReadOnlyList<CommandInfo> Recent => _recent.Reverse().ToList();

    public CommandInfo Enqueue(string name, Func<IServiceProvider, CancellationToken, Task<string?>> work)
    {
        var info = new CommandInfo { Name = name };
        _recent.Enqueue(info);
        while (_recent.Count > 50 && _recent.TryDequeue(out _))
        {
        }

        _channel.Writer.TryWrite((info, work));
        _ = eventHub.PublishAsync(EventNames.CommandUpdated, info);
        return info;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (info, work) in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            info.Status = CommandStatus.Running;
            info.Started = DateTimeOffset.UtcNow;
            await eventHub.PublishAsync(EventNames.CommandUpdated, info, stoppingToken);
            try
            {
                using var scope = scopeFactory.CreateScope();
                info.Message = await work(scope.ServiceProvider, stoppingToken);
                info.Status = CommandStatus.Completed;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Command {Command} failed", info.Name);
                info.Status = CommandStatus.Failed;
                info.Message = ex.Message;
            }

            info.Ended = DateTimeOffset.UtcNow;
            await eventHub.PublishAsync(EventNames.CommandUpdated, info, stoppingToken);
        }
    }
}

public static class SchedulingServiceCollectionExtensions
{
    public static IServiceCollection AddKrakarrScheduling(this IServiceCollection services)
    {
        services.AddSingleton<CommandQueue>();
        services.AddSingleton<ICommandQueue>(sp => sp.GetRequiredService<CommandQueue>());
        services.AddHostedService(sp => sp.GetRequiredService<CommandQueue>());
        services.AddSingleton<ScheduledTaskRunner>();
        services.AddSingleton<IScheduledTaskManager>(sp => sp.GetRequiredService<ScheduledTaskRunner>());
        services.AddHostedService(sp => sp.GetRequiredService<ScheduledTaskRunner>());
        return services;
    }

    public static IServiceCollection AddScheduledTask<T>(this IServiceCollection services)
        where T : class, IScheduledTask
    {
        services.AddScoped<T>();
        services.AddSingleton(new ScheduledTaskRegistration(typeof(T)));
        return services;
    }
}
