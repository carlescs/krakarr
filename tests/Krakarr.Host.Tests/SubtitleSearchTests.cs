using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Jobs;
using Krakarr.Data;
using Krakarr.Subtitles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Krakarr.Host.Tests;

/// <summary>Listing subtitle providers and filtering "search all missing" by selected providers.</summary>
public sealed class SubtitleSearchTests(KrakarrFactory factory) : IClassFixture<KrakarrFactory>
{
    private sealed class StubProvider(string name, string displayName, Func<SubtitleSettings, bool> isConfigured) : ISubtitleProvider
    {
        public int Searches { get; private set; }
        public string Name => name;
        public string DisplayName => displayName;
        public bool IsConfigured(SubtitleSettings settings) => isConfigured(settings);

        public Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleQuery query, SubtitleSettings settings, CancellationToken ct = default)
        {
            Searches++;
            return Task.FromResult<IReadOnlyList<SubtitleCandidate>>([]);
        }

        public Task<SubtitleContent> DownloadAsync(SubtitleCandidate candidate, SubtitleSettings settings, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> TestAsync(SubtitleSettings settings, CancellationToken ct = default) => Task.FromResult("ok");
    }

    [Fact]
    public async Task Lists_providers_and_filters_search_all_missing_to_selected_providers()
    {
        var ct = TestContext.Current.CancellationToken;
        var openSubtitles = new StubProvider("opensubtitles", "OpenSubtitles", s => s.OpenSubtitles.Enabled && !string.IsNullOrWhiteSpace(s.OpenSubtitles.ApiKey));
        var subdl = new StubProvider("subdl", "SubDL", s => s.Subdl.Enabled && !string.IsNullOrWhiteSpace(s.Subdl.ApiKey));

        using var app = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddHostedService(sp => sp.GetRequiredService<CommandQueue>());
            s.RemoveAll<ISubtitleProvider>();
            s.AddSingleton<ISubtitleProvider>(openSubtitles);
            s.AddSingleton<ISubtitleProvider>(subdl);
        }));

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            var profileId = await db.QualityProfiles.MinAsync(p => p.Id, ct);
            var movie = new Movie { TmdbId = 900123, Title = "Provider Filter Test", Year = 2024, Path = "/nonexistent/provider-filter", QualityProfileId = profileId };
            db.Movies.Add(movie);
            await db.SaveChangesAsync(ct);
            db.MovieFiles.Add(new MovieFile { MovieId = movie.Id, RelativePath = "movie.mkv", QualityId = 17 });
            await db.SaveChangesAsync(ct);

            await scope.ServiceProvider.GetRequiredService<ISettingsService>().SaveAsync(new SubtitleSettings
            {
                Enabled = true,
                Languages = ["en"],
                OpenSubtitles = new OpenSubtitlesSettings { Enabled = true, ApiKey = "os-key" },
                Subdl = new SubdlSettings { Enabled = true, ApiKey = "subdl-key" },
            }, ct);
        }

        var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", ct);
        var auth = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        (await client.PostAsJsonAsync(auth, new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, ct)).EnsureSuccessStatusCode();

        var providers = (await client.GetFromJsonAsync<JsonElement>("/api/v1/subtitles/providers", ct)).EnumerateArray().ToList();
        Assert.Equal(["opensubtitles", "subdl"], providers.Select(p => p.GetProperty("name").GetString()));
        Assert.Equal(["OpenSubtitles", "SubDL"], providers.Select(p => p.GetProperty("displayName").GetString()));
        Assert.All(providers, p => Assert.True(p.GetProperty("configured").GetBoolean()));

        // Unknown provider name returns 400.
        var bad = await client.PostAsJsonAsync("/api/v1/subtitles/search", new { providers = new[] { "unknown" } }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Search all missing restricted to SubDL only queries SubDL.
        var accepted = await client.PostAsJsonAsync("/api/v1/subtitles/search", new { providers = new[] { "subdl" } }, ct);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var command = await accepted.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("Search all missing subtitles (SubDL)", command.GetProperty("name").GetString());
        var commandId = command.GetProperty("id").GetString();

        // Wait for the background command to finish.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var commands = (await client.GetFromJsonAsync<JsonElement>("/api/v1/system/commands", ct)).EnumerateArray();
            var match = commands.FirstOrDefault(c => c.GetProperty("id").GetString() == commandId);
            if (match.ValueKind == JsonValueKind.Object && match.GetProperty("status").GetString() is "Completed" or "Failed")
            {
                Assert.Equal("Completed", match.GetProperty("status").GetString());
                break;
            }
            await Task.Delay(50, ct);
        }

        Assert.Equal(0, openSubtitles.Searches);
        Assert.True(subdl.Searches >= 1);
    }
}
