using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Krakarr.Core.Domain;
using Krakarr.Core.Push;
using Krakarr.Data;
using Krakarr.Host.Push;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Host.Tests;

/// <summary>Records what would be pushed instead of queueing it.</summary>
public sealed class RecordingPushNotifier : IPushNotifier
{
    public ConcurrentQueue<PushMessage> Sent { get; } = new();

    public void Send(PushMessage message) => Sent.Enqueue(message);
}

/// <summary>A browser's side of Web Push: its key pair and auth secret, and decrypting what the server sent.</summary>
internal sealed class FakeBrowser : IDisposable
{
    private readonly ECDiffieHellman _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _auth = RandomNumberGenerator.GetBytes(16);

    public string P256dh => WebEncoders.Base64UrlEncode(PublicKey);
    public string Auth => WebEncoders.Base64UrlEncode(_auth);
    private byte[] PublicKey => [0x04, .. _key.ExportParameters(false).Q.X!, .. _key.ExportParameters(false).Q.Y!];

    /// <summary>RFC 8291 section 3.4 and RFC 8188, from the user agent's side.</summary>
    public byte[] Decrypt(byte[] body)
    {
        var salt = body[..16];
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16)));
        var idLength = body[20];
        var asPublic = body[21..(21 + idLength)];
        var ciphertext = body[(21 + idLength)..];

        using var sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..] },
        });
        var ecdh = _key.DeriveRawSecretAgreement(sender.PublicKey);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdh, 32, _auth, [.. "WebPush: info\0"u8, .. PublicKey, .. asPublic]);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, "Content-Encoding: nonce\0"u8.ToArray());

        var plain = new byte[ciphertext.Length - 16];
        using var gcm = new AesGcm(cek, 16);
        gcm.Decrypt(nonce, ciphertext[..^16], ciphertext[^16..], plain);
        Assert.Equal(0x02, plain[^1]); // last record, no padding
        return plain[..^1];
    }

    public void Dispose() => _key.Dispose();
}

public sealed class WebPushCryptoTests
{
    [Fact]
    public void Browsers_can_decrypt_what_is_sent()
    {
        using var browser = new FakeBrowser();
        var payload = WebPushSender.Payload("Dune (2021)", "Downloaded in Bluray-1080p", "/movies/1", "movie-1");

        var body = WebPushCrypto.Encrypt(payload, browser.P256dh, browser.Auth);

        var json = JsonDocument.Parse(browser.Decrypt(body)).RootElement;
        Assert.Equal("Dune (2021)", json.GetProperty("title").GetString());
        Assert.Equal("Downloaded in Bluray-1080p", json.GetProperty("body").GetString());
        Assert.Equal("/movies/1", json.GetProperty("link").GetString());
        Assert.Equal("movie-1", json.GetProperty("tag").GetString());

        // A fresh salt and key every time.
        Assert.NotEqual(body, WebPushCrypto.Encrypt(payload, browser.P256dh, browser.Auth));
        Assert.True(WebPushCrypto.Encrypt(new byte[WebPushCrypto.MaxPayload], browser.P256dh, browser.Auth).Length <= 4096);
    }

    [Fact]
    public void Rejects_keys_that_are_not_P256_points()
    {
        using var browser = new FakeBrowser();
        Assert.Throws<ArgumentException>(() => WebPushCrypto.Encrypt("x"u8, WebEncoders.Base64UrlEncode(new byte[65]), browser.Auth));
        Assert.Throws<ArgumentException>(() => WebPushCrypto.Encrypt("x"u8, browser.P256dh, WebEncoders.Base64UrlEncode(new byte[4])));
    }

    [Fact]
    public void Vapid_token_is_signed_for_the_push_service_origin()
    {
        var keys = VapidKeys.Generate();
        var now = new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);

        var header = WebPushCrypto.VapidAuthorization(new Uri("https://fcm.googleapis.com/fcm/send/abc"), keys, "https://example.org", now);

        Assert.StartsWith("vapid t=", header);
        Assert.EndsWith($", k={keys.PublicKey}", header);
        var token = header["vapid t=".Length..header.IndexOf(',')].Split('.');
        var claims = JsonDocument.Parse(WebEncoders.Base64UrlDecode(token[1])).RootElement;
        Assert.Equal("https://fcm.googleapis.com", claims.GetProperty("aud").GetString());
        Assert.Equal(now.AddHours(12).ToUnixTimeSeconds(), claims.GetProperty("exp").GetInt64());
        Assert.Equal("https://example.org", claims.GetProperty("sub").GetString());

        var pub = WebEncoders.Base64UrlDecode(keys.PublicKey);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = pub[1..33], Y = pub[33..] },
        });
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes($"{token[0]}.{token[1]}"), WebEncoders.Base64UrlDecode(token[2]), HashAlgorithmName.SHA256));
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/abc", true)]
    [InlineData("https://web.push.apple.com/QGx", true)]
    [InlineData("https://wns2-db5p.notify.windows.com/w/?token=abc", true)]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc", false)]
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/abc", false)]
    [InlineData("https://evilfcm.googleapis.com.example.org/x", false)]
    [InlineData("https://192.168.1.10/x", false)]
    [InlineData("https://someone@fcm.googleapis.com/x", false)] // userinfo of any kind
    [InlineData("not a url", false)]
    public void Only_posts_to_browser_push_services(string endpoint, bool allowed) =>
        Assert.Equal(allowed, WebPushSender.IsAllowedEndpoint(endpoint));
}

public sealed class PushFactory : KrakarrFactory
{
    public RecordingPushNotifier Push { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton<IPushNotifier>(Push));
    }
}

public sealed class PushEndpointTests(PushFactory factory) : IClassFixture<PushFactory>
{
    private const string AdminPassword = "correct-horse";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient NewClient() => factory.CreateClient(new() { HandleCookies = true });

    private async Task<HttpClient> AdminAsync()
    {
        var client = NewClient();
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", Ct);
        var creds = new { username = "admin", password = AdminPassword, setupToken = KrakarrFactory.TestSetupToken };
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(url, creds, Ct)).StatusCode);
        return client;
    }

    private async Task<(HttpClient Client, int Id)> UserAsync(string username, string role = "User")
    {
        var admin = await AdminAsync();
        var res = await admin.PostAsJsonAsync("/api/v1/users", new { username, password = "password-123", role }, Ct);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32();
        var client = NewClient();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password = "password-123" }, Ct)).StatusCode);
        return (client, id);
    }

    private static string Endpoint() => $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";

    private static string[] Topics(JsonElement device) => device.GetProperty("topics").EnumerateArray().Select(t => t.GetString()!).ToArray();

    [Fact]
    public async Task Users_register_update_and_remove_their_devices()
    {
        var (alice, _) = await UserAsync("push-alice");
        using var browser = new FakeBrowser();

        var status = await alice.GetFromJsonAsync<JsonElement>("/api/v1/push", Ct);
        var publicKey = status.GetProperty("publicKey").GetString()!;
        Assert.Equal(65, WebEncoders.Base64UrlDecode(publicKey).Length);
        Assert.Equal(["Available", "RequestResults"], status.GetProperty("availableTopics").EnumerateArray().Select(t => t.GetString()));
        Assert.Empty(status.GetProperty("devices").EnumerateArray());

        // The key is created once and kept.
        Assert.Equal(publicKey, (await (await AdminAsync()).GetFromJsonAsync<JsonElement>("/api/v1/push", Ct)).GetProperty("publicKey").GetString());

        var endpoint = Endpoint();
        var res = await alice.PostAsJsonAsync("/api/v1/push/devices", new { endpoint, p256dh = browser.P256dh, auth = browser.Auth, name = "Pixel" }, Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var device = await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = device.GetProperty("id").GetInt32();
        Assert.Equal("Pixel", device.GetProperty("name").GetString());
        Assert.Equal(["Available", "RequestResults"], Topics(device));

        // Staff topics are dropped for plain users.
        res = await alice.PutAsJsonAsync($"/api/v1/push/devices/{id}", new { topics = new[] { "Available", "Failed", "Health" } }, Ct);
        Assert.Equal(["Available"], Topics(await res.Content.ReadFromJsonAsync<JsonElement>(Ct)));

        // Subscribing again from the same browser updates the device instead of adding another.
        res = await alice.PostAsJsonAsync("/api/v1/push/devices", new { endpoint, p256dh = browser.P256dh, auth = browser.Auth }, Ct);
        Assert.Equal(id, (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32());
        var devices = (await alice.GetFromJsonAsync<JsonElement>("/api/v1/push", Ct)).GetProperty("devices").EnumerateArray().ToList();
        Assert.Equal("Pixel", Assert.Single(devices).GetProperty("name").GetString());

        // Nobody else can touch it.
        var (bob, _) = await UserAsync("push-bob");
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/v1/push/devices/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PutAsJsonAsync($"/api/v1/push/devices/{id}", new { topics = Array.Empty<string>() }, Ct)).StatusCode);
        await bob.DeleteAsync($"/api/v1/push/devices?endpoint={Uri.EscapeDataString(endpoint)}", Ct);

        // Signing out forgets the device by its endpoint.
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/v1/push/devices?endpoint={Uri.EscapeDataString(endpoint)}", Ct)).StatusCode);
        Assert.Empty((await alice.GetFromJsonAsync<JsonElement>("/api/v1/push", Ct)).GetProperty("devices").EnumerateArray());
    }

    [Fact]
    public async Task Refuses_endpoints_outside_browser_push_services_and_bad_keys()
    {
        var admin = await AdminAsync();
        using var browser = new FakeBrowser();

        var res = await admin.PostAsJsonAsync("/api/v1/push/devices", new { endpoint = "https://192.168.1.2/admin", p256dh = browser.P256dh, auth = browser.Auth }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        res = await admin.PostAsJsonAsync("/api/v1/push/devices", new { endpoint = Endpoint(), p256dh = "abc", auth = browser.Auth }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        // Staff get every topic their role covers except all imports.
        res = await admin.PostAsJsonAsync("/api/v1/push/devices", new { endpoint = Endpoint(), p256dh = browser.P256dh, auth = browser.Auth }, Ct);
        Assert.Equal(["Available", "Failed", "Requests", "RequestResults", "Health"], Topics(await res.Content.ReadFromJsonAsync<JsonElement>(Ct)));
    }

    [Fact]
    public async Task Sends_each_event_to_the_devices_that_want_it()
    {
        var (_, userId) = await UserAsync("target-user");
        var (_, otherId) = await UserAsync("target-other");
        var (_, managerId) = await UserAsync("target-manager", "Manager");
        var (_, adminId) = await UserAsync("target-admin", "Admin");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var profile = await db.QualityProfiles.MinAsync(p => p.Id, Ct);
        var movie = new Movie { TmdbId = 9_001, Title = "Followed", Year = 2020, Path = "/m/f", QualityProfileId = profile };
        db.Movies.Add(movie);
        await db.SaveChangesAsync(Ct);
        db.MediaSubscriptions.Add(new MediaSubscription { UserId = userId, MovieId = movie.Id });

        PushSubscription Device(int owner, PushTopics topics)
        {
            var s = new PushSubscription { UserId = owner, Endpoint = Endpoint(), P256dh = "k", Auth = "a", Topics = topics };
            db.PushSubscriptions.Add(s);
            return s;
        }

        var user = Device(userId, PushTopics.Available | PushTopics.RequestResults | PushTopics.Imported | PushTopics.Health);
        var userMuted = Device(userId, PushTopics.None);
        var other = Device(otherId, PushTopics.All);
        var manager = Device(managerId, PushTopics.All);
        var managerQuiet = Device(managerId, PushTopics.Failed);
        var admin = Device(adminId, PushTopics.Health | PushTopics.Imported);
        await db.SaveChangesAsync(Ct);

        async Task<int[]> TargetsAsync(PushMessage m) =>
            (await PushDispatcher.Targets(db, m).Select(t => t.Id).ToListAsync(Ct)).Intersect(new[] { user, userMuted, other, manager, managerQuiet, admin }.Select(d => d.Id)).ToArray();

        // Imports: staff who want every import, plus followers of the title (once each, even when both apply).
        Assert.Equal([user.Id, manager.Id, admin.Id], await TargetsAsync(new PushMessage(PushKind.Imported, "Followed", null, "/movies/1", MovieId: movie.Id)));
        Assert.Equal([manager.Id, admin.Id], await TargetsAsync(new PushMessage(PushKind.Imported, "Other", null, "/movies/2", MovieId: movie.Id + 1)));
        Assert.Equal([manager.Id, managerQuiet.Id], await TargetsAsync(new PushMessage(PushKind.ImportFailed, "x", null, "/activity")));
        Assert.Equal([manager.Id], await TargetsAsync(new PushMessage(PushKind.RequestCreated, "x", null, "/requests")));
        Assert.Equal([user.Id], await TargetsAsync(new PushMessage(PushKind.RequestResult, "x", null, "/requests", UserId: userId)));
        // Health is for admins only, whatever plain users or managers chose.
        Assert.Equal([admin.Id], await TargetsAsync(new PushMessage(PushKind.Health, "x", null, "/settings/health")));
    }
}
