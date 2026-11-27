using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Push;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Push;

/// <summary>
/// The VAPID key pair, created on first use. Not exposed through the settings endpoints: only the public key leaves
/// the server (<c>GET /api/v1/push</c>). Backups keep it, so restored instances stay subscribed.
/// </summary>
public sealed record PushSettings : ISettingsSection
{
    public static string SectionKey => "push";
    public string? VapidPublicKey { get; init; }
    public string? VapidPrivateKey { get; init; }

    /// <summary>Contact for push service operators, sent in every VAPID token. Apple rejects tokens without a valid one.</summary>
    public string Subject { get; init; } = "https://github.com/carlescs/krakarr";
}

/// <summary>Reads the VAPID keys, generating and saving them the first time.</summary>
public sealed class VapidKeyStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<(VapidKeys Keys, string Subject)> GetAsync(ISettingsService settings, CancellationToken ct)
    {
        var current = await settings.GetAsync<PushSettings>(ct);
        if (current is { VapidPublicKey: { } pub, VapidPrivateKey: { } priv }) return (new VapidKeys(pub, priv), current.Subject);

        await _gate.WaitAsync(ct);
        try
        {
            current = await settings.GetAsync<PushSettings>(ct);
            if (current is { VapidPublicKey: { } pub2, VapidPrivateKey: { } priv2 }) return (new VapidKeys(pub2, priv2), current.Subject);
            var keys = VapidKeys.Generate();
            await settings.SaveAsync(current with { VapidPublicKey = keys.PublicKey, VapidPrivateKey = keys.PrivateKey }, ct);
            return (keys, current.Subject);
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Only the columns needed to deliver to a device.</summary>
public sealed record PushTarget(int Id, string Endpoint, string P256dh, string Auth);

public enum PushDelivery
{
    Delivered,
    /// <summary>The push service no longer knows the device (permission revoked, app removed): drop the subscription.</summary>
    Gone,
    Failed,
}

/// <summary>Posts encrypted messages to push services.</summary>
public sealed class WebPushSender(IHttpClientFactory httpFactory, TimeProvider time, ILogger<WebPushSender> logger)
{
    public const string HttpClientName = "webpush";

    /// <summary>How long a push service keeps a message for a device that is offline.</summary>
    private static readonly TimeSpan TimeToLive = TimeSpan.FromDays(1);

    /// <summary>
    /// Browser push services. Endpoints come from the browser, but the server is the one posting to them, so they are
    /// limited to these hosts (and subdomains) rather than letting a user point Krakarr at any URL on the network.
    /// </summary>
    private static readonly string[] PushServiceHosts =
    [
        "fcm.googleapis.com", // Chrome, Edge on Android, Opera, Samsung Internet
        "android.googleapis.com",
        "push.services.mozilla.com", // Firefox
        "push.apple.com", // Safari, iOS and iPadOS home screen apps
        "notify.windows.com", // Edge on Windows
    ];

    public static bool IsAllowedEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && PushServiceHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    /// <summary>The JSON the service worker reads (<c>web/public/sw.js</c>).</summary>
    public static byte[] Payload(string title, string? body, string link, string? tag) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string?>
        {
            ["title"] = Truncate(title, 200),
            ["body"] = body is null ? null : Truncate(body, 1000),
            ["link"] = link,
            ["tag"] = tag,
        });

    public async Task<PushDelivery> SendAsync(PushTarget target, byte[] payload, VapidKeys keys, string subject, CancellationToken ct)
    {
        if (!IsAllowedEndpoint(target.Endpoint)) return PushDelivery.Gone;
        try
        {
            var endpoint = new Uri(target.Endpoint);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(WebPushCrypto.Encrypt(payload, target.P256dh, target.Auth)),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentEncoding.Add("aes128gcm");
            request.Headers.TryAddWithoutValidation("Authorization", WebPushCrypto.VapidAuthorization(endpoint, keys, subject, time.GetUtcNow()));
            request.Headers.Add("TTL", ((int)TimeToLive.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.Add("Urgency", "normal");

            // Only the status matters: the body (an error description at most) is never read.
            using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode) return PushDelivery.Delivered;
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return PushDelivery.Gone;
            logger.LogWarning("Push service {Host} answered {Status}", endpoint.Host, (int)response.StatusCode);
            return PushDelivery.Failed;
        }
        catch (ArgumentException ex)
        {
            // Keys the browser sent that aren't valid P-256 material: the subscription can never work.
            logger.LogWarning("Dropping a push subscription with invalid keys: {Message}", ex.Message);
            return PushDelivery.Gone;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Could not reach the push service: {Message}", ex.Message);
            return PushDelivery.Failed;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : string.Concat(s.AsSpan(0, max - 1), "…");
}

/// <summary>
/// Queues push messages (<see cref="IPushNotifier.Send"/> returns at once) and delivers them in the background: works
/// out which devices want each one, then posts to their push services one after the other. The queue is bounded; if
/// push services are so slow that it fills up, the oldest messages are dropped rather than holding memory.
/// </summary>
public sealed class PushDispatcher(
    IServiceScopeFactory scopeFactory,
    WebPushSender sender,
    VapidKeyStore keyStore,
    ILogger<PushDispatcher> logger) : BackgroundService, IPushNotifier
{
    private readonly Channel<PushMessage> _queue = Channel.CreateBounded<PushMessage>(new BoundedChannelOptions(200)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    public void Send(PushMessage message) => _queue.Writer.TryWrite(message);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await DeliverAsync(message, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not send the push notification \"{Title}\"", message.Title);
            }
        }
    }

    /// <summary>Sends <paramref name="message"/> to every device that wants it. Returns how many got it.</summary>
    public async Task<int> DeliverAsync(PushMessage message, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var targets = await Targets(db, message).ToListAsync(ct);
        if (targets.Count == 0) return 0;

        var (keys, subject) = await keyStore.GetAsync(scope.ServiceProvider.GetRequiredService<ISettingsService>(), ct);
        var payload = WebPushSender.Payload(message.Title, message.Body, message.Link, message.Tag);
        var (delivered, gone) = (0, new List<int>());
        foreach (var target in targets)
        {
            switch (await sender.SendAsync(target, payload, keys, subject, ct))
            {
                case PushDelivery.Delivered: delivered++; break;
                case PushDelivery.Gone: gone.Add(target.Id); break;
            }
        }

        if (gone.Count > 0) await db.PushSubscriptions.Where(s => gone.Contains(s.Id)).ExecuteDeleteAsync(ct);
        return delivered;
    }

    /// <summary>The devices that want <paramref name="m"/>, given their topics and their user's role.</summary>
    public static IQueryable<PushTarget> Targets(KrakarrDbContext db, PushMessage m)
    {
        var subs = db.PushSubscriptions.AsNoTracking();
        subs = m.Kind switch
        {
            PushKind.Imported => subs.Where(s =>
                ((s.Topics & PushTopics.Imported) != 0 && (s.User!.Role == UserRole.Admin || s.User.Role == UserRole.Manager))
                || ((s.Topics & PushTopics.Available) != 0 && db.MediaSubscriptions.Any(f => f.UserId == s.UserId
                    && ((m.MovieId != null && f.MovieId == m.MovieId) || (m.SeriesId != null && f.SeriesId == m.SeriesId))))),
            PushKind.ImportFailed => subs.Where(s =>
                (s.Topics & PushTopics.Failed) != 0 && (s.User!.Role == UserRole.Admin || s.User.Role == UserRole.Manager)),
            PushKind.RequestCreated => subs.Where(s =>
                (s.Topics & PushTopics.Requests) != 0 && (s.User!.Role == UserRole.Admin || s.User.Role == UserRole.Manager)),
            PushKind.RequestResult => subs.Where(s => (s.Topics & PushTopics.RequestResults) != 0 && s.UserId == m.UserId),
            PushKind.Health => subs.Where(s => (s.Topics & PushTopics.Health) != 0 && s.User!.Role == UserRole.Admin),
            _ => subs.Where(_ => false),
        };
        return subs.OrderBy(s => s.Id).Select(s => new PushTarget(s.Id, s.Endpoint, s.P256dh, s.Auth));
    }
}

public static class PushServiceCollectionExtensions
{
    public static IServiceCollection AddKrakarrPush(this IServiceCollection services)
    {
        services.AddHttpClient(WebPushSender.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<VapidKeyStore>();
        services.AddSingleton<WebPushSender>();
        services.AddSingleton<PushDispatcher>();
        services.AddSingleton<IPushNotifier>(sp => sp.GetRequiredService<PushDispatcher>());
        services.AddHostedService(sp => sp.GetRequiredService<PushDispatcher>());
        return services;
    }
}
