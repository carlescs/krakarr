using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Krakarr.Host.Auth;

/// <summary>Brute-force protection and session settings (section <c>Krakarr:Auth</c>).</summary>
public sealed class AuthSecurityOptions
{
    public const string SectionName = "Krakarr:Auth";

    /// <summary>Requests per minute and client IP to the login, setup and password endpoints.</summary>
    public int AttemptsPerMinutePerIp { get; set; } = 10;

    /// <summary>Consecutive failed logins for one username before it is temporarily locked.</summary>
    public int FailuresBeforeLockout { get; set; } = 5;

    /// <summary>First lockout; doubles with every further failure up to <see cref="MaxLockout"/>.</summary>
    public TimeSpan InitialLockout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxLockout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>A sign-in expires after this long without any request (sliding).</summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// A sign-in expires this long after the password was entered, however active it is, so a stolen cookie can't be
    /// kept alive forever. Zero or negative disables the limit.
    /// </summary>
    public TimeSpan SessionMaxAge { get; set; } = TimeSpan.FromDays(30);
}

/// <summary>
/// Tracks failed logins per username and locks the username with exponential backoff once
/// <see cref="AuthSecurityOptions.FailuresBeforeLockout"/> is reached. Complements the per-IP rate limit, which a
/// distributed attacker could otherwise spread across many addresses. Lockouts are temporary (capped), so the owner
/// can never be locked out for longer than <see cref="AuthSecurityOptions.MaxLockout"/>.
/// </summary>
public sealed class LoginThrottle(IOptions<AuthSecurityOptions> options, TimeProvider time)
{
    private const int MaxTrackedUsernames = 10_000;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset LockedUntil;
        public DateTimeOffset LastFailure;
    }

    /// <summary>Remaining lockout for <paramref name="username"/>, or null when a login attempt is allowed.</summary>
    public TimeSpan? RetryAfter(string username)
    {
        if (!_entries.TryGetValue(Key(username), out var entry)) return null;
        lock (entry)
        {
            var remaining = entry.LockedUntil - time.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : null;
        }
    }

    public void RecordFailure(string username)
    {
        if (_entries.Count >= MaxTrackedUsernames) Prune();
        var o = options.Value;
        var entry = _entries.GetOrAdd(Key(username), _ => new Entry());
        lock (entry)
        {
            var now = time.GetUtcNow();
            entry.Failures++;
            entry.LastFailure = now;
            if (entry.Failures >= o.FailuresBeforeLockout)
            {
                var exponent = Math.Min(entry.Failures - o.FailuresBeforeLockout, 20);
                var lockout = TimeSpan.FromTicks(Math.Min(o.InitialLockout.Ticks * (1L << exponent), o.MaxLockout.Ticks));
                entry.LockedUntil = now + lockout;
            }
        }
    }

    public void RecordSuccess(string username) => _entries.TryRemove(Key(username), out _);

    private void Prune()
    {
        var cutoff = time.GetUtcNow() - options.Value.MaxLockout - TimeSpan.FromHours(1);
        foreach (var (key, entry) in _entries)
        {
            if (entry.LastFailure < cutoff && entry.LockedUntil < time.GetUtcNow()) _entries.TryRemove(key, out _);
        }

        // Still full (e.g. username spraying): forget the oldest half rather than growing without bound.
        if (_entries.Count >= MaxTrackedUsernames)
        {
            foreach (var key in _entries.OrderBy(e => e.Value.LastFailure).Take(_entries.Count / 2).Select(e => e.Key).ToList())
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private static string Key(string username) => username.Trim().ToLowerInvariant();
}
