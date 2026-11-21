using Microsoft.Extensions.Logging.Abstractions;

namespace Krakarr.Host.Telemetry;

/// <summary>
/// Logging provider that forwards to the OpenTelemetry log exporter while log export is on, and drops everything
/// otherwise. It is registered once at startup; <see cref="TelemetryManager"/> swaps the inner factory when the
/// telemetry settings change, so logs can be turned on and off without a restart. Level filtering
/// (Logging:LogLevel in appsettings) applies before anything gets here.
/// </summary>
[ProviderAlias("OpenTelemetry")]
public sealed class TelemetryLoggerProvider : ILoggerProvider
{
    private Target? _target;

    public ILogger CreateLogger(string categoryName) => new ForwardingLogger(this, categoryName);

    /// <summary>Starts forwarding to <paramref name="factory"/> (null stops) and returns the previous factory for disposal.</summary>
    internal ILoggerFactory? Swap(ILoggerFactory? factory) =>
        Interlocked.Exchange(ref _target, factory is null ? null : new Target(factory))?.Factory;

    public void Dispose() => Swap(null)?.Dispose();

    private sealed class Target(ILoggerFactory factory)
    {
        public ILoggerFactory Factory { get; } = factory;
    }

    private sealed class ForwardingLogger(TelemetryLoggerProvider provider, string category) : ILogger
    {
        private (Target Target, ILogger Logger)? _cached;

        private ILogger? Current()
        {
            var target = Volatile.Read(ref provider._target);
            if (target is null) return null;
            var cached = _cached;
            if (cached is { } c && ReferenceEquals(c.Target, target)) return c.Logger;
            var logger = target.Factory.CreateLogger(category);
            _cached = (target, logger);
            return logger;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            Current()?.BeginScope(state) ?? NullLogger.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && Volatile.Read(ref provider._target) is not null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Current()?.Log(logLevel, eventId, state, exception, formatter);
    }
}
