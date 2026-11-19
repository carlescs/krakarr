using System.Collections.Concurrent;
using System.Threading.Channels;
using Krakarr.Core.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Krakarr.Core.Jobs;

/// <summary>A task that runs periodically inside its own DI scope.</summary>
public interface IScheduledTask
{
    string Name { get; }
    TimeSpan Interval { get; }
    TimeSpan InitialDelay => TimeSpan.FromSeconds(10);
    Task ExecuteAsync(CancellationToken cancellationToken);
}

public sealed class ScheduledTaskRunner(
    IServiceScopeFactory scopeFactory,
    IEnumerable<ScheduledTaskRegistration> registrations,
    ILogger<ScheduledTaskRunner> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(registrations.Select(r => RunLoopAsync(r.TaskType, stoppingToken)));

    private async Task RunLoopAsync(Type taskType, CancellationToken ct)
    {
        TimeSpan delay;
        using (var scope = scopeFactory.CreateScope())
        {
            delay = ((IScheduledTask)scope.ServiceProvider.GetRequiredService(taskType)).InitialDelay;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var task = (IScheduledTask)scope.ServiceProvider.GetRequiredService(taskType);
            delay = task.Interval;
            try
            {
                await task.ExecuteAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled task {Task} failed", task.Name);
            }
        }
    }
}

public sealed record ScheduledTaskRegistration(Type TaskType);

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
        services.AddHostedService<ScheduledTaskRunner>();
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
