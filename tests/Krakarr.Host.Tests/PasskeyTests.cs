using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Krakarr.Host.Auth.Passkeys;

namespace Krakarr.Host.Tests;

public sealed class WebAuthnTests
{
    private const string RpId = "krakarr.example.com";
    private const string Origin = "https://krakarr.example.com";

    [Theory]
    [InlineData("https://krakarr.example.com", true)]
    [InlineData("https://krakarr.example.com:8443", true)]
    [InlineData("https://sub.krakarr.example.com", true)]
    [InlineData("http://krakarr.example.com", false)]
    [InlineData("https://evil-krakarr.example.com", false)]
    [InlineData("https://krakarr.example.com.evil.net", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void Origin_must_be_https_on_the_rp_id(string? origin, bool valid) => Assert.Equal(valid, WebAuthn.IsValidOrigin(origin, RpId));

    [Fact]
    public void Localhost_may_use_http() => Assert.True(WebAuthn.IsValidOrigin("http://localhost:7878", "localhost"));

    [Theory]
    [InlineData("192.168.1.10", null)]
    [InlineData("[::1]", null)]
    [InlineData("Krakarr.Example.com", "krakarr.example.com")]
    [InlineData("localhost", "localhost")]
    public void Rp_id_is_the_host_name(string host, string? expected) => Assert.Equal(expected, WebAuthn.RpIdFor(host));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registers_and_verifies_assertions(bool rsa)
    {
        using var key = new SoftwareAuthenticator(rsa);
        var (clientData, attestation) = key.Create("reg", RpId, Origin);
        var credential = WebAuthn.VerifyRegistration(clientData, attestation, RpId, c => c == "reg");
        Assert.Equal(key.CredentialId, credential.CredentialId);

        key.SignCount = 5;
        var (cd, ad, sig) = key.Get("login", RpId, Origin);
        Assert.Equal(5, WebAuthn.VerifyAssertion(cd, ad, sig, credential.PublicKey, 0, RpId, c => c == "login"));

        // Tampered signature, or a counter that doesn't move forward.
        sig[^1] ^= 0xFF;
        Assert.Throws<WebAuthnException>(() => WebAuthn.VerifyAssertion(cd, ad, sig, credential.PublicKey, 0, RpId, _ => true));
        (cd, ad, sig) = key.Get("login", RpId, Origin);
        Assert.Throws<WebAuthnException>(() => WebAuthn.VerifyAssertion(cd, ad, sig, credential.PublicKey, 5, RpId, _ => true));
    }

    [Fact]
    public void Synced_passkeys_with_a_zero_counter_are_accepted()
    {
        using var key = new SoftwareAuthenticator();
        var (cd, ad, sig) = key.Get("c", RpId, Origin);
        Assert.Equal(0, WebAuthn.VerifyAssertion(cd, ad, sig, key.CoseKey(), 0, RpId, _ => true));
    }

    [Fact]
    public void Rejects_wrong_site_challenge_type_or_missing_verification()
    {
        using var key = new SoftwareAuthenticator();
        var cose = key.CoseKey();

        var (cd, ad, sig) = key.Get("c", "other.example.com", "https://other.example.com");
        Assert.Throws<WebAuthnException>(() => WebAuthn.VerifyAssertion(cd, ad, sig, cose, 0, RpId, _ => true));

        (cd, ad, sig) = key.Get("c", RpId, Origin);
        Assert.Throws<WebAuthnException>(() => WebAuthn.VerifyAssertion(cd, ad, sig, cose, 0, RpId, c => c == "other"));

        var (createData, attestation) = key.Create("c", RpId, Origin);
        Assert.Throws<WebAuthnException>(() => WebAuthn.VerifyAssertion(createData, ad, sig, cose, 0, RpId, _ => true));
        Assert.Throws<WebAuthnException>(() => WebAuthn.VerifyRegistration(cd, attestation, RpId, _ => true));

        key.Flags = 0x01; // present, not verified
        (cd, ad, sig) = key.Get("c", RpId, Origin);
        Assert.Throws<WebAuthnException>(() => WebAuthn.VerifyAssertion(cd, ad, sig, cose, 0, RpId, _ => true));
    }

    [Fact]
    public void Rejects_malformed_input()
    {
        Assert.Throws<WebAuthnException>(() => WebAuthn.FromBase64Url("***"));
        Assert.Throws<WebAuthnException>(() =>
            WebAuthn.VerifyRegistration(SoftwareAuthenticator.ClientData("webauthn.create", "c", Origin), [0xFF, 0x00], RpId, _ => true));
        Assert.Throws<WebAuthnException>(() =>
            WebAuthn.VerifyAssertion("{".Select(c => (byte)c).ToArray(), new byte[37], [], [], 0, RpId, _ => true));
    }
}

/// <summary>Own host so the accounts and passkeys created here don't interfere with other test classes.</summary>
public sealed class PasskeyFactory : KrakarrFactory;

public sealed class PasskeyTests(PasskeyFactory factory) : IClassFixture<PasskeyFactory>
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

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = NewClient();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password }, Ct)).StatusCode);
        return client;
    }

    /// <summary>Registers <paramref name="key"/> for the signed-in user and returns the new passkey's id.</summary>
    private static async Task<int> RegisterAsync(HttpClient client, SoftwareAuthenticator key, string password, string name = "Phone")
    {
        var options = await client.PostAsJsonAsync("/api/v1/auth/passkeys/register/options", new { currentPassword = password }, Ct);
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        var json = await options.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("localhost", json.GetProperty("rp").GetProperty("id").GetString());
        Assert.Equal("required", json.GetProperty("authenticatorSelection").GetProperty("userVerification").GetString());
        key.UserHandle = WebAuthn.FromBase64Url(json.GetProperty("user").GetProperty("id").GetString());

        var res = await client.PostAsJsonAsync("/api/v1/auth/passkeys", key.RegistrationRequest(name, json.GetProperty("challenge").GetString()!), Ct);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32();
    }

    private async Task<string> LoginChallengeAsync(HttpClient client)
    {
        var res = await client.PostAsync("/api/v1/auth/passkeys/login/options", null, Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("localhost", json.GetProperty("rpId").GetString());
        return json.GetProperty("challenge").GetString()!;
    }

    [Fact]
    public async Task Signs_in_with_a_registered_passkey()
    {
        var admin = await AdminAsync();
        using var key = new SoftwareAuthenticator();

        // Registering needs the current password.
        var wrong = await admin.PostAsJsonAsync("/api/v1/auth/passkeys/register/options", new { currentPassword = "nope" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var id = await RegisterAsync(admin, key, AdminPassword);

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/v1/auth/passkeys", Ct);
        var entry = list.EnumerateArray().Single(p => p.GetProperty("id").GetInt32() == id);
        Assert.Equal("Phone", entry.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("lastUsedAt").ValueKind);

        // The same authenticator can't be registered twice.
        var options = await (await admin.PostAsJsonAsync("/api/v1/auth/passkeys/register/options", new { currentPassword = AdminPassword }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Contains(options.GetProperty("excludeCredentials").EnumerateArray(),
            c => c.GetProperty("id").GetString() == WebAuthn.ToBase64Url(key.CredentialId));
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync("/api/v1/auth/passkeys", key.RegistrationRequest("Again", options.GetProperty("challenge").GetString()!), Ct)).StatusCode);

        var client = NewClient();
        var body = key.SignInRequest(await LoginChallengeAsync(client));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/passkeys/login", body, Ct)).StatusCode);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", Ct);
        Assert.True(status.GetProperty("authenticated").GetBoolean());
        Assert.Equal("admin", status.GetProperty("username").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/users", Ct)).StatusCode);

        // Challenges are single-use, so a captured response can't be replayed.
        Assert.Equal(HttpStatusCode.Unauthorized, (await NewClient().PostAsJsonAsync("/api/v1/auth/passkeys/login", body, Ct)).StatusCode);

        list = await admin.GetFromJsonAsync<JsonElement>("/api/v1/auth/passkeys", Ct);
        Assert.NotEqual(JsonValueKind.Null, list.EnumerateArray().Single(p => p.GetProperty("id").GetInt32() == id).GetProperty("lastUsedAt").ValueKind);
    }

    [Fact]
    public async Task Rejects_responses_for_another_site_unknown_passkeys_and_wrong_users()
    {
        var admin = await AdminAsync();
        using var key = new SoftwareAuthenticator();
        await RegisterAsync(admin, key, AdminPassword);

        var client = NewClient();
        var otherSite = key.SignInRequest(await LoginChallengeAsync(client), rpId: "evil.example.com");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/passkeys/login", otherSite, Ct)).StatusCode);

        using var unknown = new SoftwareAuthenticator();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/v1/auth/passkeys/login", unknown.SignInRequest(await LoginChallengeAsync(client)), Ct)).StatusCode);

        key.UserHandle = "999"u8.ToArray();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/v1/auth/passkeys/login", key.SignInRequest(await LoginChallengeAsync(client)), Ct)).StatusCode);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", Ct);
        Assert.False(status.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task Passkeys_are_private_renamable_and_removable()
    {
        var admin = await AdminAsync();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/users", new { username = "viewer", password = "password-123", role = "User" }, Ct)).StatusCode);
        var viewer = await LoginAsync("viewer", "password-123");
        using var key = new SoftwareAuthenticator(rsa: true);
        var id = await RegisterAsync(viewer, key, "password-123", "Laptop");

        Assert.Equal(HttpStatusCode.Unauthorized, (await NewClient().GetAsync("/api/v1/auth/passkeys", Ct)).StatusCode);
        var adminList = await admin.GetFromJsonAsync<JsonElement>("/api/v1/auth/passkeys", Ct);
        Assert.DoesNotContain(adminList.EnumerateArray(), p => p.GetProperty("id").GetInt32() == id);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/v1/auth/passkeys/{id}", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await viewer.PutAsJsonAsync($"/api/v1/auth/passkeys/{id}", new { name = "Work laptop" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.PutAsJsonAsync($"/api/v1/auth/passkeys/{id}", new { name = " " }, Ct)).StatusCode);
        var list = await viewer.GetFromJsonAsync<JsonElement>("/api/v1/auth/passkeys", Ct);
        Assert.Equal("Work laptop", list.EnumerateArray().Single().GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await viewer.DeleteAsync($"/api/v1/auth/passkeys/{id}", Ct)).StatusCode);
        var client = NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/v1/auth/passkeys/login", key.SignInRequest(await LoginChallengeAsync(client)), Ct)).StatusCode);
    }

    [Fact]
    public async Task Password_reset_by_an_admin_removes_the_users_passkeys()
    {
        var admin = await AdminAsync();
        var created = await admin.PostAsJsonAsync("/api/v1/users", new { username = "resetme", password = "password-123", role = "User" }, Ct);
        var userId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32();
        var user = await LoginAsync("resetme", "password-123");
        using var key = new SoftwareAuthenticator();
        await RegisterAsync(user, key, "password-123");

        Assert.Equal(HttpStatusCode.OK,
            (await admin.PutAsJsonAsync($"/api/v1/users/{userId}", new { username = "resetme", role = "User", password = "brand-new-pass" }, Ct)).StatusCode);

        var client = NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/v1/auth/passkeys/login", key.SignInRequest(await LoginChallengeAsync(client)), Ct)).StatusCode);
        var again = await LoginAsync("resetme", "brand-new-pass");
        Assert.Empty((await again.GetFromJsonAsync<JsonElement>("/api/v1/auth/passkeys", Ct)).EnumerateArray());
    }

    [Fact]
    public async Task Passkeys_need_a_host_name()
    {
        var client = NewClient();
        client.DefaultRequestHeaders.Host = "192.168.1.10";
        var res = await client.PostAsync("/api/v1/auth/passkeys/login/options", null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
