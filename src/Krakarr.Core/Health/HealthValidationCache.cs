using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Krakarr.Core.Health;

/// <summary>
/// Remembers the outcome of health validations that need the network (is this API key accepted?), so the checks that
/// run every few minutes call the remote service only when the configuration changed, once a day after a success, or
/// once an hour after a failure. One entry per validation name, so it is bounded by the number of checks.
/// </summary>
public sealed class HealthValidationCache(TimeProvider time)
{
    public static readonly TimeSpan SuccessLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan FailureLifetime = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// The cached outcome of validation <paramref name="name"/> for <paramref name="configuration"/> (e.g. the API key;
    /// only a hash of it is kept), or the result of <paramref name="validate"/> when there is none or it is too old.
    /// </summary>
    /// <param name="validate">Returns the problem, or null when the configuration works.</param>
    public async Task<HealthProblem?> GetAsync(string name, string configuration, Func<CancellationToken, Task<HealthProblem?>> validate, CancellationToken ct)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuration)));
        var now = time.GetUtcNow();
        if (_entries.TryGetValue(name, out var entry) && entry.Fingerprint == fingerprint
            && now - entry.CheckedAt < (entry.Problem is null ? SuccessLifetime : FailureLifetime))
        {
            return entry.Problem;
        }

        var problem = await validate(ct);
        _entries[name] = new Entry(fingerprint, problem, now);
        return problem;
    }

    /// <summary>Forgets every outcome, so the next run validates again (the user asked to check now).</summary>
    public void Clear() => _entries.Clear();

    private sealed record Entry(string Fingerprint, HealthProblem? Problem, DateTimeOffset CheckedAt);
}
