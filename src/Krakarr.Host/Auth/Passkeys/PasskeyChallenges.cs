using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Krakarr.Host.Auth.Passkeys;

/// <summary>
/// Pending WebAuthn challenges. Each is random, single-use and expires after <see cref="Lifetime"/>; the store is
/// capped at <see cref="Capacity"/> entries (pruned on every add), so anonymous sign-in attempts can't grow it.
/// </summary>
public sealed class PasskeyChallenges(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public const int Capacity = 500;

    /// <param name="UserId">The user registering a passkey; null for sign-in.</param>
    private sealed record Pending(int? UserId, string RpId, DateTimeOffset Expires);

    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    /// <summary>Issues a challenge (base64url), or null when too many are pending.</summary>
    public string? Create(int? userId, string rpId)
    {
        var now = time.GetUtcNow();
        if (_pending.Count >= Capacity)
        {
            foreach (var (key, value) in _pending)
            {
                if (value.Expires <= now) _pending.TryRemove(key, out _);
            }

            if (_pending.Count >= Capacity) return null;
        }

        var challenge = WebAuthn.ToBase64Url(RandomNumberGenerator.GetBytes(32));
        _pending[challenge] = new Pending(userId, rpId, now + Lifetime);
        return challenge;
    }

    /// <summary>Consumes <paramref name="challenge"/>; true when it was issued for this user and RP id and hasn't expired.</summary>
    public bool Take(string challenge, int? userId, string rpId) =>
        _pending.TryRemove(challenge, out var pending)
        && pending.UserId == userId
        && pending.RpId == rpId
        && pending.Expires > time.GetUtcNow();
}
