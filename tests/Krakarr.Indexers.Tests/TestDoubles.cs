using Krakarr.Core.Health;

namespace Krakarr.Indexers.Tests;

internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class RecordingHealthMonitor : IHealthMonitor
{
    public int Requests { get; private set; }
    public HealthReport Current => HealthReport.Empty;
    public void RequestCheck() => Requests++;
    public Task<HealthReport> CheckNowAsync(CancellationToken ct = default) => Task.FromResult(HealthReport.Empty);
}
