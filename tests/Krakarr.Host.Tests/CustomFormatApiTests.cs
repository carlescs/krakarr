using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Krakarr.Host.Tests;

public sealed class CustomFormatApiTests(KrakarrFactory factory) : IClassFixture<KrakarrFactory>
{
    private async Task<HttpClient> LoggedInClientAsync()
    {
        var client = factory.CreateClient(new() { HandleCookies = true });
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", TestContext.Current.CancellationToken);
        var creds = new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken };
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(url, creds, TestContext.Current.CancellationToken)).StatusCode);
        return client;
    }

    [Fact]
    public async Task Create_score_import_export_and_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await LoggedInClientAsync();

        var bad = await client.PostAsJsonAsync("/api/v1/customformats",
            new { name = "Bad", specifications = new[] { new { condition = "ReleaseTitle", value = "([" } } }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var created = await client.PostAsJsonAsync("/api/v1/customformats",
            new { name = "Atmos", specifications = new[] { new { condition = "Audio", value = "Atmos" } } }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var atmosId = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetInt32();

        // Profiles score formats; unknown formats are refused.
        var profile = (await client.GetFromJsonAsync<JsonElement[]>("/api/v1/qualityprofiles", ct))![0];
        var update = new
        {
            name = profile.GetProperty("name").GetString(),
            allowedQualityIds = profile.GetProperty("allowedQualityIds").EnumerateArray().Select(e => e.GetInt32()).ToArray(),
            cutoffQualityId = profile.GetProperty("cutoffQualityId").GetInt32(),
            upgradeAllowed = true,
            formatScores = new[] { new { formatId = atmosId, score = 150 } },
            minFormatScore = -100,
            cutoffFormatScore = 150,
        };
        var profileId = profile.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/qualityprofiles/{profileId}", update, ct)).StatusCode);
        var unknown = update with { formatScores = new[] { new { formatId = 99999, score = 1 } } };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/v1/qualityprofiles/{profileId}", unknown, ct)).StatusCode);

        // TRaSH JSON: new formats are created, existing names replaced, unsupported ones reported.
        const string json = """
            [
              { "name": "Atmos", "specifications": [{ "name": "Atmos", "implementation": "ReleaseTitleSpecification", "fields": { "value": "\\bATMOS\\b" } }] },
              { "name": "x265", "specifications": [{ "name": "x265", "implementation": "ReleaseTitleSpecification", "fields": { "value": "[xh][ ._-]?265" } }] },
              { "name": "Freeleech", "specifications": [{ "name": "FL", "implementation": "IndexerFlagSpecification", "fields": { "value": 1 } }] }
            ]
            """;
        var imported = await client.PostAsJsonAsync("/api/v1/customformats/import", new { json, app = "Radarr" }, ct);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var result = await imported.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(1, result.GetProperty("created").GetInt32());
        Assert.Equal(1, result.GetProperty("updated").GetInt32());
        Assert.Single(result.GetProperty("warnings").EnumerateArray());

        var test = await client.PostAsJsonAsync("/api/v1/customformats/test", new { title = "Movie.2020.2160p.BluRay.TrueHD.7.1.Atmos.x265-GRP" }, ct);
        var matched = (await test.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("formats").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToList();
        Assert.Equal(["Atmos", "x265"], matched);

        var export = await client.GetStringAsync("/api/v1/customformats/export?app=Radarr", ct);
        var exported = JsonDocument.Parse(export).RootElement;
        Assert.Contains(exported.EnumerateArray(), f => f.GetProperty("name").GetString() == "x265"
            && f.GetProperty("specifications")[0].GetProperty("implementation").GetString() == "ReleaseTitleSpecification");

        // Deleting a format drops its score from profiles.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/customformats/{atmosId}", ct)).StatusCode);
        var after = (await client.GetFromJsonAsync<JsonElement[]>("/api/v1/qualityprofiles", ct))!.Single(p => p.GetProperty("id").GetInt32() == profileId);
        Assert.Empty(after.GetProperty("formatScores").EnumerateArray());
    }
}
