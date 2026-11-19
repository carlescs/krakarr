using Krakarr.Core.Configuration;
using Krakarr.Downloads.QBittorrent;

namespace Krakarr.Downloads.Tests;

public class QBittorrentApiTests
{
    private static readonly QBittorrentSettings Settings = new() { Url = "http://qbt.local:8080", Username = "admin", Password = "secret", Tag = "krakarr" };

    [Fact]
    public async Task Logs_in_and_sends_session_cookie()
    {
        var fake = new FakeQBittorrent();
        using var api = new QBittorrentApi(Settings, fake);
        Assert.Equal("v4.6.3", await api.GetVersionAsync(TestContext.Current.CancellationToken));
        Assert.Equal("/api/v2/auth/login", fake.Requests[0].Path);
        Assert.Contains("username=admin", fake.Requests[0].Body);
        Assert.Equal("SID=sid-1", fake.Requests[1].Cookie);
    }

    [Fact]
    public async Task Wrong_password_is_reported()
    {
        using var api = new QBittorrentApi(Settings with { Password = "nope" }, new FakeQBittorrent());
        var ex = await Assert.ThrowsAsync<QBittorrentException>(() => api.LoginAsync(TestContext.Current.CancellationToken));
        Assert.Contains("username or password", ex.Message);
    }

    [Fact]
    public async Task Re_authenticates_once_after_403()
    {
        var fake = new FakeQBittorrent();
        using var api = new QBittorrentApi(Settings, fake);
        await api.LoginAsync(TestContext.Current.CancellationToken);
        fake.ForbidNextRequests = 1;
        Assert.Equal("v4.6.3", await api.GetVersionAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, fake.Requests.Count(r => r.Path == "/api/v2/auth/login"));
        Assert.Equal("SID=sid-2", fake.Requests[^1].Cookie);
    }

    [Fact]
    public async Task Lists_only_tagged_torrents_and_filters_by_tag()
    {
        var fake = new FakeQBittorrent();
        fake.Torrents.Add(new("aaa", "Ours", "krakarr"));
        fake.Torrents.Add(new("bbb", "Ours too", "other, Krakarr"));
        fake.Torrents.Add(new("ccc", "Not ours", "linux-isos"));
        fake.Torrents.Add(new("ddd", "Untagged", ""));
        using var api = new QBittorrentApi(Settings, fake);

        var torrents = await api.GetTorrentsAsync("krakarr", ct: TestContext.Current.CancellationToken);

        Assert.Equal(["aaa", "bbb"], torrents.Select(t => t.Hash));
        Assert.Contains("tag=krakarr", fake.Requests.Single(r => r.Path == "/api/v2/torrents/info").Query);
    }

    [Fact]
    public async Task Add_sends_tag_category_and_save_path()
    {
        var fake = new FakeQBittorrent();
        using var api = new QBittorrentApi(Settings, fake);
        await api.AddAsync("magnet:?xt=urn:btih:abc", null, "krakarr", "movies", "/data/downloads", paused: false, TestContext.Current.CancellationToken);

        var add = fake.Requests.Single(r => r.Path == "/api/v2/torrents/add");
        Assert.Contains("name=tags", add.Body);
        Assert.Contains("krakarr", add.Body);
        Assert.Contains("name=category", add.Body);
        Assert.Contains("movies", add.Body);
        Assert.Contains("/data/downloads", add.Body);
        Assert.Contains("magnet:?xt=urn:btih:abc", add.Body);
    }

    [Theory]
    [InlineData("2.9.3", "/api/v2/torrents/pause", "/api/v2/torrents/resume")]
    [InlineData("2.11.2", "/api/v2/torrents/stop", "/api/v2/torrents/start")]
    public async Task Uses_pause_or_stop_depending_on_webapi_version(string version, string pausePath, string resumePath)
    {
        var fake = new FakeQBittorrent { WebApiVersion = version };
        using var api = new QBittorrentApi(Settings, fake);
        await api.PauseAsync("aaa", TestContext.Current.CancellationToken);
        await api.ResumeAsync("aaa", TestContext.Current.CancellationToken);
        Assert.Contains(fake.Requests, r => r.Path == pausePath);
        Assert.Contains(fake.Requests, r => r.Path == resumePath);
    }

    [Theory]
    [InlineData("metaDL", 0, Krakarr.Core.Domain.DownloadState.FetchingMetadata)]
    [InlineData("downloading", 0.4, Krakarr.Core.Domain.DownloadState.Downloading)]
    [InlineData("stalledDL", 0.4, Krakarr.Core.Domain.DownloadState.Downloading)]
    [InlineData("queuedDL", 0, Krakarr.Core.Domain.DownloadState.Queued)]
    [InlineData("pausedDL", 0.4, Krakarr.Core.Domain.DownloadState.Paused)]
    [InlineData("stoppedDL", 0.4, Krakarr.Core.Domain.DownloadState.Paused)]
    [InlineData("uploading", 1, Krakarr.Core.Domain.DownloadState.Seeding)]
    [InlineData("stalledUP", 1, Krakarr.Core.Domain.DownloadState.Seeding)]
    [InlineData("pausedUP", 1, Krakarr.Core.Domain.DownloadState.Completed)]
    [InlineData("stoppedUP", 1, Krakarr.Core.Domain.DownloadState.Completed)]
    [InlineData("missingFiles", 1, Krakarr.Core.Domain.DownloadState.Error)]
    [InlineData("moving", 1, Krakarr.Core.Domain.DownloadState.Seeding)]
    public void Maps_states(string state, double progress, Krakarr.Core.Domain.DownloadState expected)
    {
        Assert.Equal(expected, QBittorrentEngine.MapState(state, progress));
    }

    [Theory]
    [InlineData("/downloads/movies/Film", "/downloads", "/data/downloads", "/data/downloads/movies/Film")]
    [InlineData("/downloads", "/downloads/", "/data/downloads", "/data/downloads")]
    [InlineData("/other/path", "/downloads", "/data/downloads", "/other/path")]
    [InlineData("/downloadsX/a", "/downloads", "/data/downloads", "/downloadsX/a")]
    [InlineData(@"D:\Torrents\Film", @"D:\Torrents", "/data/downloads", "/data/downloads/Film")]
    [InlineData("/downloads/a", "", "/x", "/downloads/a")]
    public void Maps_remote_paths(string remote, string remotePrefix, string localPrefix, string expected)
    {
        var settings = new QBittorrentSettings { RemotePathPrefix = remotePrefix, LocalPathPrefix = localPrefix };
        Assert.Equal(expected, QBittorrentEngine.MapPath(remote, settings));
    }

    [Fact]
    public void Unmaps_local_paths()
    {
        var settings = new QBittorrentSettings { RemotePathPrefix = "/downloads", LocalPathPrefix = "/data/downloads" };
        Assert.Equal("/downloads/movies", QBittorrentEngine.UnmapPath("/data/downloads/movies", settings));
    }
}
