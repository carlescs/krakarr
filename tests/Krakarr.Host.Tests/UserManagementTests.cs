using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Krakarr.Core.Domain;
using Krakarr.Host.Auth;

namespace Krakarr.Host.Tests;

public sealed class RoleAccessTests
{
    [Theory]
    [InlineData("GET", "/api/v1/movies", UserRole.User)]
    [InlineData("GET", "/api/v1/movies/5", UserRole.User)]
    [InlineData("GET", "/api/v1/movies/discover/popular", UserRole.User)]
    [InlineData("GET", "/api/v1/series/3/episodes", UserRole.User)]
    [InlineData("GET", "/api/v1/series/3/episodes/7/details", UserRole.User)]
    [InlineData("GET", "/api/v1/calendar", UserRole.User)]
    [InlineData("GET", "/api/v1/people/6384", UserRole.User)]
    [InlineData("GET", "/api/v1/people/lookup", UserRole.User)]
    [InlineData("GET", "/api/v1/keywords/lookup", UserRole.User)]
    [InlineData("GET", "/api/v1/movies/discover/keywords", UserRole.User)]
    [InlineData("POST", "/api/v1/keywords/lookup", UserRole.Manager)]
    [InlineData("GET", "/api/v1/movies/tmdb/603", UserRole.User)]
    [InlineData("GET", "/api/v1/series/tmdb/1396/similar", UserRole.User)]
    [InlineData("GET", "/api/v1/qualityprofiles", UserRole.User)]
    [InlineData("GET", "/api/v1/subtitles/files", UserRole.User)]
    [InlineData("GET", "/api/v1/subtitles/wanted-languages", UserRole.User)]
    [InlineData("POST", "/api/v1/auth/password", UserRole.User)]
    [InlineData("DELETE", "/api/v1/auth/passkeys/3", UserRole.User)]
    [InlineData("POST", "/hubs/events/negotiate", UserRole.User)]
    [InlineData("GET", "/movies/5", UserRole.User)] // SPA routes
    [InlineData("POST", "/api/v1/movies", UserRole.Manager)]
    [InlineData("GET", "/api/v1/system/background-tasks", UserRole.User)] // filtered to the caller's own tasks
    [InlineData("POST", "/api/v1/system/background-tasks/subtitles.sync.4/cancel", UserRole.User)]
    [InlineData("DELETE", "/api/v1/system/background-tasks", UserRole.User)]
    [InlineData("GET", "/api/v1/system/tasks", UserRole.Admin)]
    [InlineData("DELETE", "/api/v1/movies/5", UserRole.Manager)]
    [InlineData("GET", "/api/v1/movies/5/releases", UserRole.Manager)]
    [InlineData("GET", "/api/v1/movies/lookup", UserRole.User)]
    [InlineData("GET", "/api/v1/series/lookup", UserRole.User)]
    [InlineData("POST", "/api/v1/series/import", UserRole.Manager)]
    [InlineData("GET", "/api/v1/subscriptions", UserRole.User)]
    [InlineData("PUT", "/api/v1/subscriptions/Movie/5", UserRole.User)]
    [InlineData("DELETE", "/api/v1/subscriptions/Series/5", UserRole.User)]
    [InlineData("GET", "/api/v1/subscriptions/users", UserRole.Manager)]
    [InlineData("POST", "/api/v1/subscriptions/bulk", UserRole.User)]
    [InlineData("POST", "/api/v1/subscriptions/series/unfollow-finished", UserRole.User)]
    [InlineData("POST", "/api/v1/series/finished/unmonitor", UserRole.Admin)]
    [InlineData("PUT", "/api/v1/movies/bulk", UserRole.Manager)]
    [InlineData("POST", "/api/v1/movies/bulk/delete", UserRole.Manager)]
    [InlineData("POST", "/api/v1/series/bulk/search", UserRole.Manager)]
    [InlineData("GET", "/api/v1/series/import/scan", UserRole.Manager)]
    [InlineData("GET", "/api/v1/series/3/organize", UserRole.Manager)]
    [InlineData("PUT", "/api/v1/episodes/1", UserRole.Manager)]
    [InlineData("GET", "/api/v1/history", UserRole.Manager)]
    [InlineData("GET", "/api/v1/downloads", UserRole.Manager)]
    [InlineData("GET", "/api/v1/search", UserRole.Manager)]
    [InlineData("GET", "/api/v1/search/indexers", UserRole.Manager)]
    [InlineData("GET", "/api/v1/system/commands", UserRole.Manager)]
    [InlineData("GET", "/api/v1/subtitles/wanted", UserRole.Manager)]
    [InlineData("GET", "/api/v1/subtitles/providers", UserRole.Manager)]
    [InlineData("GET", "/api/v1/rootfolders", UserRole.Manager)]
    [InlineData("GET", "/api/v1/something-new", UserRole.Manager)] // closed to plain users by default
    [InlineData("GET", "/api/v1/requests", UserRole.User)]
    [InlineData("GET", "/api/v1/notifications", UserRole.User)]
    [InlineData("POST", "/api/v1/requests", UserRole.User)]
    [InlineData("DELETE", "/api/v1/requests/4", UserRole.User)]
    [InlineData("POST", "/api/v1/requests/4/approve", UserRole.Manager)]
    [InlineData("POST", "/api/v1/requests/4/decline", UserRole.Manager)]
    [InlineData("GET", "/api/v1/settings/general", UserRole.Admin)]
    [InlineData("GET", "/API/V1/SETTINGS/general", UserRole.Admin)]
    [InlineData("GET", "/api/v1/users", UserRole.Admin)]
    [InlineData("GET", "/api/v1/indexers", UserRole.Admin)]
    [InlineData("GET", "/api/v1/filesystem", UserRole.Admin)]
    [InlineData("GET", "/api/v1/downloads/client", UserRole.Admin)]
    [InlineData("GET", "/api/v1/system/status", UserRole.Admin)]
    [InlineData("POST", "/api/v1/system/tasks/rss-sync/run", UserRole.Admin)]
    [InlineData("GET", "/api/v1/system/backups", UserRole.Admin)]
    [InlineData("GET", "/api/v1/system/backups/krakarr_backup_20261125_101500_manual.zip", UserRole.Admin)]
    [InlineData("POST", "/api/v1/system/backups/restore", UserRole.Admin)]
    [InlineData("GET", "/api/v1/system/health", UserRole.Admin)]
    [InlineData("POST", "/api/v1/system/health/check", UserRole.Admin)]
    [InlineData("GET", "/api/v1/health", UserRole.User)]
    [InlineData("POST", "/api/v1/qualityprofiles", UserRole.Admin)]
    [InlineData("DELETE", "/api/v1/rootfolders/1", UserRole.Admin)]
    [InlineData("POST", "/api/v1/subtitles/providers/test", UserRole.Admin)]
    public void Required_role(string method, string path, UserRole expected) =>
        Assert.Equal(expected, RoleAccess.RequiredRole(method, path));

    [Fact]
    public void Prefixes_match_whole_segments() =>
        Assert.Equal(UserRole.Manager, RoleAccess.RequiredRole("GET", "/api/v1/moviesx"));
}

/// <summary>Own host so the accounts created here don't interfere with other test classes.</summary>
public sealed class UsersFactory : KrakarrFactory;

public sealed class UserManagementTests(UsersFactory factory) : IClassFixture<UsersFactory>
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

    private async Task<int> CreateUserAsync(HttpClient admin, string username, string role, string password = "password-123")
    {
        var res = await admin.PostAsJsonAsync("/api/v1/users", new { username, password, role }, Ct);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32();
    }

    private async Task<HttpClient> LoginAsync(string username, string password = "password-123")
    {
        var client = NewClient();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password }, Ct)).StatusCode);
        return client;
    }

    private static async Task<HttpStatusCode> GetStatus(HttpClient client, string url) => (await client.GetAsync(url, Ct)).StatusCode;

    [Fact]
    public async Task Setup_user_is_an_admin()
    {
        var admin = await AdminAsync();
        var status = await admin.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", Ct);
        Assert.Equal("Admin", status.GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.OK, await GetStatus(admin, "/api/v1/users"));
    }

    [Fact]
    public async Task Roles_limit_what_each_user_can_do()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, "viewer", "User");
        await CreateUserAsync(admin, "boss", "Manager");
        var viewer = await LoginAsync("viewer");
        var manager = await LoginAsync("boss");

        Assert.Equal("User", (await viewer.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", Ct)).GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.OK, await GetStatus(viewer, "/api/v1/movies"));
        Assert.Equal(HttpStatusCode.OK, await GetStatus(viewer, "/api/v1/series"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetStatus(viewer, "/api/v1/history"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetStatus(viewer, "/api/v1/downloads"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetStatus(viewer, "/api/v1/settings/general"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetStatus(viewer, "/api/v1/users"));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 1, qualityProfileId = 1, rootFolderPath = "/x" }, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, await GetStatus(manager, "/api/v1/history"));
        Assert.Equal(HttpStatusCode.OK, await GetStatus(manager, "/api/v1/rootfolders"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetStatus(manager, "/api/v1/settings/general"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetStatus(manager, "/api/v1/users"));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await manager.PostAsJsonAsync("/api/v1/users", new { username = "x", password = "password-123", role = "Admin" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Role_changes_and_password_resets_apply_to_existing_sessions()
    {
        var admin = await AdminAsync();
        var id = await CreateUserAsync(admin, "promoted", "User");
        var session = await LoginAsync("promoted");
        Assert.Equal(HttpStatusCode.Forbidden, await GetStatus(session, "/api/v1/history"));

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/users/{id}", new { username = "promoted", role = "Manager" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await GetStatus(session, "/api/v1/history"));

        // Resetting the password signs the user out everywhere.
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PutAsJsonAsync($"/api/v1/users/{id}", new { username = "promoted", role = "Manager", password = "brand-new-pass" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await GetStatus(session, "/api/v1/movies"));
        await LoginAsync("promoted", "brand-new-pass");

        // Deleting the account does too.
        var again = await LoginAsync("promoted", "brand-new-pass");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/users/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await GetStatus(again, "/api/v1/movies"));
    }

    [Fact]
    public async Task There_is_always_an_admin_and_usernames_are_unique()
    {
        var admin = await AdminAsync();
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/v1/users", Ct);
        var adminId = users.EnumerateArray().Single(u => u.GetProperty("username").GetString() == "admin").GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/v1/users/{adminId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/v1/users/{adminId}", new { username = "admin", role = "User" }, Ct)).StatusCode);

        var second = await CreateUserAsync(admin, "second-admin", "Admin");
        var secondClient = await LoginAsync("second-admin");
        Assert.Equal(HttpStatusCode.OK, (await secondClient.PutAsJsonAsync($"/api/v1/users/{adminId}", new { username = "admin", role = "Manager" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await secondClient.PutAsJsonAsync($"/api/v1/users/{second}", new { username = "second-admin", role = "User" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await secondClient.PutAsJsonAsync($"/api/v1/users/{adminId}", new { username = "admin", role = "Admin" }, Ct)).StatusCode);

        var duplicate = await admin.PostAsJsonAsync("/api/v1/users", new { username = "SECOND-ADMIN", password = "password-123", role = "User" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        var shortPassword = await admin.PostAsJsonAsync("/api/v1/users", new { username = "shorty", password = "short", role = "User" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);
    }

    [Fact]
    public async Task Changing_own_password_requires_the_current_one()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, "self-service", "User");
        var client = await LoginAsync("self-service");
        var other = await LoginAsync("self-service");

        var wrong = await client.PostAsJsonAsync("/api/v1/auth/password",
            new { username = "self-service", currentPassword = "not-it-at-all", newPassword = "another-pass" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var ok = await client.PostAsJsonAsync("/api/v1/auth/password",
            new { username = "self-service", currentPassword = "password-123", newPassword = "another-pass" }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        // This session stays signed in; other sessions are signed out.
        Assert.Equal(HttpStatusCode.OK, await GetStatus(client, "/api/v1/movies"));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetStatus(other, "/api/v1/movies"));
        Assert.Equal("User", (await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", Ct)).GetProperty("role").GetString());
    }

    [Fact]
    public async Task Signing_out_other_sessions_keeps_only_the_current_one()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, "lost-phone", "User");
        var current = await LoginAsync("lost-phone");
        var stolen = await LoginAsync("lost-phone");
        Assert.Equal(HttpStatusCode.OK, await GetStatus(stolen, "/api/v1/movies"));

        Assert.Equal(HttpStatusCode.NoContent, (await current.PostAsync("/api/v1/auth/sessions/revoke", null, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, await GetStatus(current, "/api/v1/movies"));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetStatus(stolen, "/api/v1/movies"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await NewClient().PostAsync("/api/v1/auth/sessions/revoke", null, Ct)).StatusCode);
    }
}
