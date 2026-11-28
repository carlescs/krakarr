using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Krakarr.Host.Auth.Passkeys;

/// <summary>
/// Pending WebAuthn challenges. Each is random, single-use and expires after <see cref="Lifetime"/>.
/// <para>
/// Sign-in and registration use separate pools, so anonymous sign-in attempts can't crowd out signed-in users adding a
/// passkey. Both pools are bounded and never refuse a new challenge: when one is full, its oldest challenge is dropped.
/// A legitimate ceremony only needs its challenge to survive the few seconds between asking for options and answering,
/// so flooding the pool to block sign-ins would take thousands of requests per second from many addresses, which the
/// per-IP rate limit prevents. On top of that, each client (IP address, or /64 for IPv6) holds at most
/// <see cref="PerClientLimit"/> sign-in challenges and each user one registration challenge, so no single client can
/// fill a pool.
/// </para>
/// </summary>
public sealed class PasskeyChallenges(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>Maximum pending challenges per pool (sign-in and registration).</summary>
    public const int Capacity = 500;

    /// <summary>Maximum pending sign-in challenges per client address.</summary>
    public const int PerClientLimit = 5;

    /// <param name="Owner">The client address (sign-in) or user id (registration) the challenge was issued to.</param>
    private sealed record Pending(string Owner, int? UserId, string RpId, DateTimeOffset Expires, long Sequence);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Pending> _signIn = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> _registration = new(StringComparer.Ordinal);
    private long _sequence;

    /// <summary>Issues a sign-in challenge (base64url) to <paramref name="client"/>.</summary>
    public string CreateForSignIn(IPAddress? client, string rpId) =>
        Add(_signIn, ClientKey(client), null, rpId, PerClientLimit);

    /// <summary>Issues a registration challenge (base64url); it replaces any the user still had pending.</summary>
    public string CreateForRegistration(int userId, string rpId) =>
        Add(_registration, "user:" + userId.ToString(System.Globalization.CultureInfo.InvariantCulture), userId, rpId, 1);

    /// <summary>
    /// Consumes <paramref name="challenge"/>; true when it was issued for this user (null for sign-in) and RP id and
    /// hasn't expired.
    /// </summary>
    public bool Take(string challenge, int? userId, string rpId)
    {
        Pending? pending;
        lock (_lock)
        {
            if (!(userId is null ? _signIn : _registration).Remove(challenge, out pending)) return false;
        }

        return pending.UserId == userId && pending.RpId == rpId && pending.Expires > time.GetUtcNow();
    }

    private string Add(Dictionary<string, Pending> pool, string owner, int? userId, string rpId, int perOwnerLimit)
    {
        var challenge = WebAuthn.ToBase64Url(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow();
        lock (_lock)
        {
            // The pool holds at most Capacity entries, so these scans stay small.
            var owned = 0;
            KeyValuePair<string, Pending>? oldestOwned = null, oldest = null;
            foreach (var entry in pool)
            {
                if (entry.Value.Expires <= now)
                {
                    pool.Remove(entry.Key); // removing the current entry while enumerating is allowed
                    continue;
                }

                if (oldest is null || entry.Value.Sequence < oldest.Value.Value.Sequence) oldest = entry;
                if (entry.Value.Owner != owner) continue;
                owned++;
                if (oldestOwned is null || entry.Value.Sequence < oldestOwned.Value.Value.Sequence) oldestOwned = entry;
            }

            if (owned >= perOwnerLimit && oldestOwned is { } mine)
            {
                pool.Remove(mine.Key);
                if (oldest?.Key == mine.Key) oldest = null;
            }

            if (pool.Count >= Capacity && oldest is { } first) pool.Remove(first.Key);
            pool[challenge] = new Pending(owner, userId, rpId, now + Lifetime, ++_sequence);
        }

        return challenge;
    }

    /// <summary>IPv4 addresses as they are; IPv6 by /64, since one host can usually pick any address in its /64.</summary>
    private static string ClientKey(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return new IPAddress(bytes).ToString() + "/64";
    }
}
