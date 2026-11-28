using System.Net;
using Krakarr.Host.Auth.Passkeys;

namespace Krakarr.Host.Tests;

public sealed class PasskeyChallengesTests
{
    private const string RpId = "krakarr.example.com";

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly PasskeyChallenges _challenges;

    public PasskeyChallengesTests() => _challenges = new PasskeyChallenges(_time);

    private static IPAddress Ip(int i) => new([10, 0, (byte)(i >> 8), (byte)i]);

    [Fact]
    public void Challenges_are_single_use_and_bound_to_user_rp_id_and_lifetime()
    {
        var signIn = _challenges.CreateForSignIn(Ip(1), RpId);
        Assert.False(_challenges.Take(signIn, 1, RpId)); // a sign-in challenge isn't a registration challenge
        signIn = _challenges.CreateForSignIn(Ip(1), RpId);
        Assert.True(_challenges.Take(signIn, null, RpId));
        Assert.False(_challenges.Take(signIn, null, RpId));

        var reg = _challenges.CreateForRegistration(1, RpId);
        Assert.False(_challenges.Take(reg, 2, RpId));
        reg = _challenges.CreateForRegistration(1, RpId);
        Assert.False(_challenges.Take(reg, 1, "other.example.com"));

        reg = _challenges.CreateForRegistration(1, RpId);
        _time.Advance(PasskeyChallenges.Lifetime);
        Assert.False(_challenges.Take(reg, 1, RpId));
    }

    [Fact]
    public void A_full_pool_drops_its_oldest_challenge_instead_of_refusing()
    {
        var first = _challenges.CreateForSignIn(Ip(0), RpId);
        var second = _challenges.CreateForSignIn(Ip(1), RpId);
        for (var i = 2; i <= PasskeyChallenges.Capacity; i++) _challenges.CreateForSignIn(Ip(i), RpId);

        Assert.False(_challenges.Take(first, null, RpId));
        Assert.True(_challenges.Take(second, null, RpId));
    }

    [Fact]
    public void One_client_cannot_hold_more_than_its_share()
    {
        var mine = Enumerable.Range(0, PasskeyChallenges.PerClientLimit + 1).Select(_ => _challenges.CreateForSignIn(Ip(1), RpId)).ToList();
        var other = _challenges.CreateForSignIn(Ip(2), RpId);

        Assert.False(_challenges.Take(mine[0], null, RpId));
        Assert.All(mine.Skip(1), c => Assert.True(_challenges.Take(c, null, RpId)));
        Assert.True(_challenges.Take(other, null, RpId));
    }

    [Fact]
    public void Ipv6_clients_are_grouped_by_their_64()
    {
        var first = _challenges.CreateForSignIn(IPAddress.Parse("2001:db8:1:2::1"), RpId);
        for (var i = 0; i < PasskeyChallenges.PerClientLimit; i++)
        {
            _challenges.CreateForSignIn(IPAddress.Parse($"2001:db8:1:2::{i + 100:x}"), RpId);
        }

        Assert.False(_challenges.Take(first, null, RpId));
    }

    [Fact]
    public void Sign_in_flooding_does_not_touch_registrations()
    {
        var reg = _challenges.CreateForRegistration(1, RpId);
        for (var i = 0; i < PasskeyChallenges.Capacity * 2; i++) _challenges.CreateForSignIn(Ip(i), RpId);
        Assert.True(_challenges.Take(reg, 1, RpId));
    }

    [Fact]
    public void A_new_registration_challenge_replaces_the_users_previous_one()
    {
        var old = _challenges.CreateForRegistration(1, RpId);
        var other = _challenges.CreateForRegistration(2, RpId);
        var current = _challenges.CreateForRegistration(1, RpId);

        Assert.False(_challenges.Take(old, 1, RpId));
        Assert.True(_challenges.Take(current, 1, RpId));
        Assert.True(_challenges.Take(other, 2, RpId));
    }
}
