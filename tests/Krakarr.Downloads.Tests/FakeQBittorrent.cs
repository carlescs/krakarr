using System.Net;
using System.Text;
using System.Text.Json;

namespace Krakarr.Downloads.Tests;

/// <summary>In-memory stand-in for the qBittorrent WebUI API.</summary>
public sealed class FakeQBittorrent : HttpMessageHandler
{
    public sealed record FakeTorrent(string Hash, string Name, string Tags, double Progress = 0, string State = "downloading", string SavePath = "/downloads/",
        string? V1 = null, string? V2 = null);

    public List<FakeTorrent> Torrents { get; } = [];
    public List<(string Method, string Path, string Query, string Body, string? Cookie)> Requests { get; } = [];
    public string WebApiVersion { get; set; } = "2.9.3";
    public string Password { get; set; } = "secret";
    public int ForbidNextRequests { get; set; }
    private string _sid = "sid-1";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var cookie = request.Headers.TryGetValues("Cookie", out var c) ? string.Join(";", c) : null;
        Requests.Add((request.Method.Method, path, query, body, cookie));

        if (path == "/api/v2/auth/login")
        {
            if (!body.Contains($"password={Password}"))
            {
                return Text("Fails.");
            }

            var ok = Text("Ok.");
            ok.Headers.Add("Set-Cookie", $"SID={_sid}; HttpOnly; path=/");
            return ok;
        }

        if (ForbidNextRequests > 0)
        {
            ForbidNextRequests--;
            _sid = "sid-2"; // force a new session
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        }

        if (cookie != $"SID={_sid}")
        {
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        }

        switch (path)
        {
            case "/api/v2/app/version": return Text("v4.6.3");
            case "/api/v2/app/webapiVersion": return Text(WebApiVersion);
            case "/api/v2/torrents/createTags":
            case "/api/v2/torrents/createCategory":
            case "/api/v2/torrents/pause":
            case "/api/v2/torrents/resume":
            case "/api/v2/torrents/stop":
            case "/api/v2/torrents/start":
                return Text(string.Empty);
            case "/api/v2/torrents/delete":
                Torrents.RemoveAll(t => body.Contains("hashes=" + t.Hash));
                return Text(string.Empty);
            case "/api/v2/torrents/add":
                return Text("Ok.");
            case "/api/v2/torrents/info":
                var qs = System.Web.HttpUtility.ParseQueryString(query);
                var list = Torrents.Where(t => qs["hashes"] is null || t.Hash == qs["hashes"])
                    // Real qBittorrent filters by tag, but return everything here to prove the client re-checks.
                    .Select(t => new
                    {
                        hash = t.Hash, infohash_v1 = t.V1 ?? t.Hash, infohash_v2 = t.V2 ?? "", name = t.Name, state = t.State, progress = t.Progress, size = 1000L, total_size = 1000L,
                        downloaded = (long)(t.Progress * 1000), uploaded = 0L, dlspeed = 10L, upspeed = 0L, num_seeds = 1, num_leechs = 0,
                        ratio = 0.0, eta = 8640000L, category = "movies", tags = t.Tags, save_path = t.SavePath,
                        added_on = 1_700_000_000L, completion_on = t.Progress >= 1 ? 1_700_000_100L : -1L,
                    });
                return Json(list);
            case "/api/v2/torrents/properties":
                return Raw("""
                    {"save_path":"/downloads/","creation_date":1700000000,"piece_size":4194304,"comment":"Krakarr test","created_by":"mktorrent 1.1",
                     "total_wasted":1024,"total_uploaded":500,"total_uploaded_session":100,"total_downloaded":1000,"total_downloaded_session":900,
                     "up_limit":-1,"dl_limit":512000,"time_elapsed":3600,"seeding_time":600,"nb_connections":5,"nb_connections_limit":100,
                     "share_ratio":0.5,"seeds":2,"seeds_total":10,"peers":3,"peers_total":20,"pieces_have":3,"pieces_num":4,"reannounce":1500,
                     "dl_speed_avg":2048,"up_speed_avg":64,"last_seen":1700000100,"total_size":1000,"is_private":true}
                    """);
            case "/api/v2/torrents/trackers":
                return Raw("""
                    [{"url":"** [DHT] **","status":2,"tier":-1,"num_peers":12,"num_seeds":4,"num_leeches":8,"num_downloaded":-1,"msg":""},
                     {"url":"http://tracker.example/announce","status":4,"tier":0,"num_peers":0,"num_seeds":-1,"num_leeches":-1,"num_downloaded":-1,"msg":"Connection refused"}]
                    """);
            case "/api/v2/sync/torrentPeers":
                return Raw("""
                    {"full_update":true,"rid":1,"peers":{"10.0.0.5:51413":{"client":"Transmission 4.0.5","connection":"μTP","country":"Spain","country_code":"es",
                     "dl_speed":20480,"downloaded":4096000,"flags":"D X E","flags_desc":"D = Currently downloading (interested and not choked)\nX = Peer from PEX\nE = Encrypted traffic",
                     "ip":"10.0.0.5","port":51413,"progress":1,"up_speed":0,"uploaded":0}}}
                    """);
            case "/api/v2/torrents/pieceStates":
                return Raw("[2,2,1,0]");
            case "/api/v2/torrents/webseeds":
                return Raw("""[{"url":"https://mirror.example/file.mkv"}]""");
            case "/api/v2/torrents/files":
                return Json(new[] { new { name = "Movie.2020.1080p/Movie.2020.1080p.mkv", size = 900L }, new { name = "Movie.2020.1080p/info.nfo", size = 1L } });
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static HttpResponseMessage Raw(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Text(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, "text/plain") };
    private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
}
