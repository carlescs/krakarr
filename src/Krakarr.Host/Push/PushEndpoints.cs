using Krakarr.Core.Domain;
using Krakarr.Core.Push;
using Krakarr.Data;
using Krakarr.Host.Auth;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Push;

/// <summary>A device as its owner sees it in the list.</summary>
public sealed record PushDeviceDto(int Id, string? Name, string Endpoint, IReadOnlyList<PushTopics> Topics, DateTimeOffset CreatedAt);

/// <summary>What the push settings need: the key to subscribe with, the topics the caller's role can use, and their devices.</summary>
public sealed record PushStatusDto(string PublicKey, IReadOnlyList<PushTopics> AvailableTopics, IReadOnlyList<PushDeviceDto> Devices);

/// <summary>From the browser's <c>PushSubscription.toJSON()</c>; <see cref="Topics"/> defaults to <see cref="PushEndpoints.DefaultTopics"/>.</summary>
public sealed record AddPushDeviceRequest(string Endpoint, string P256dh, string Auth, string? Name = null, List<PushTopics>? Topics = null);

public sealed record UpdatePushDeviceRequest(List<PushTopics> Topics);

public sealed record PushTestResult(bool Delivered, string Message);

public static class PushEndpoints
{
    /// <summary>Most devices one user may register; adding another forgets their oldest (most likely a browser they no longer use).</summary>
    public const int MaxDevicesPerUser = 20;

    /// <summary>The topics <paramref name="role"/> receives anything for.</summary>
    public static PushTopics TopicsFor(UserRole role) => role switch
    {
        UserRole.Admin => PushTopics.All,
        UserRole.Manager => PushTopics.All & ~PushTopics.Health,
        _ => PushTopics.Available | PushTopics.RequestResults,
    };

    /// <summary>What a new device gets: everything the role covers except every single import, which is noisy on a busy library.</summary>
    public static PushTopics DefaultTopics(UserRole role) => TopicsFor(role) & ~PushTopics.Imported;

    public static IEndpointRouteBuilder MapPushEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/push").WithTags("Push");

        group.MapGet("/", async (HttpContext http, KrakarrDbContext db, ISettingsService settings, VapidKeyStore keys, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } userId) return NoUser();
            var (vapid, _) = await keys.GetAsync(settings, ct);
            var devices = await db.PushSubscriptions.AsNoTracking().Where(s => s.UserId == userId).OrderBy(s => s.CreatedAt)
                .Select(s => new { s.Id, s.Name, s.Endpoint, s.Topics, s.CreatedAt }).ToListAsync(ct);
            return Results.Ok(new PushStatusDto(vapid.PublicKey, List(TopicsFor(http.User.GetRole())),
                devices.Select(d => new PushDeviceDto(d.Id, d.Name, d.Endpoint, List(d.Topics), d.CreatedAt)).ToList()));
        });

        // Registers this browser, or updates it when it is registered already (possibly by another account that signed in
        // on the same device, which loses it: a device belongs to whoever subscribed last).
        group.MapPost("/devices", async (AddPushDeviceRequest req, HttpContext http, KrakarrDbContext db, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } userId) return NoUser();
            if (Validate(req) is { } error) return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);

            var role = http.User.GetRole();
            var topics = req.Topics is null ? DefaultTopics(role) : Combine(req.Topics) & TopicsFor(role);
            var name = string.IsNullOrWhiteSpace(req.Name) ? null : Truncate(req.Name.Trim(), PushSubscriptionLimits.Name);
            var device = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == req.Endpoint, ct);
            if (device is null)
            {
                var stale = await db.PushSubscriptions.Where(s => s.UserId == userId).OrderByDescending(s => s.CreatedAt)
                    .Skip(MaxDevicesPerUser - 1).Select(s => s.Id).ToListAsync(ct);
                if (stale.Count > 0) await db.PushSubscriptions.Where(s => stale.Contains(s.Id)).ExecuteDeleteAsync(ct);
                device = new PushSubscription { UserId = userId, Endpoint = req.Endpoint, P256dh = req.P256dh, Auth = req.Auth };
                db.PushSubscriptions.Add(device);
            }

            device.UserId = userId;
            device.P256dh = req.P256dh;
            device.Auth = req.Auth;
            device.Topics = topics;
            device.Name = name ?? device.Name;
            await db.SaveChangesAsync(ct);
            return Results.Ok(Dto(device));
        });

        group.MapPut("/devices/{id:int}", async (int id, UpdatePushDeviceRequest req, HttpContext http, KrakarrDbContext db, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } userId) return NoUser();
            var device = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
            if (device is null) return Results.NotFound();
            device.Topics = Combine(req.Topics) & TopicsFor(http.User.GetRole());
            await db.SaveChangesAsync(ct);
            return Results.Ok(Dto(device));
        });

        group.MapDelete("/devices/{id:int}", async (int id, HttpContext http, KrakarrDbContext db, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } userId) return NoUser();
            var deleted = await db.PushSubscriptions.Where(s => s.Id == id && s.UserId == userId).ExecuteDeleteAsync(ct);
            return deleted > 0 ? Results.NoContent() : Results.NotFound();
        });

        // Forgets this browser by its endpoint: signing out calls it so a shared device stops getting the account's notifications.
        group.MapDelete("/devices", async (string endpoint, HttpContext http, KrakarrDbContext db, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } userId) return NoUser();
            await db.PushSubscriptions.Where(s => s.Endpoint == endpoint && s.UserId == userId).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        // Sends a test notification to one device straight away and says how it went.
        group.MapPost("/devices/{id:int}/test", async (int id, HttpContext http, KrakarrDbContext db, ISettingsService settings,
            VapidKeyStore keys, WebPushSender sender, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } userId) return NoUser();
            var target = await db.PushSubscriptions.AsNoTracking().Where(s => s.Id == id && s.UserId == userId)
                .Select(s => new PushTarget(s.Id, s.Endpoint, s.P256dh, s.Auth)).FirstOrDefaultAsync(ct);
            if (target is null) return Results.NotFound();

            var (vapid, subject) = await keys.GetAsync(settings, ct);
            var payload = WebPushSender.Payload("Krakarr", "Push notifications work on this device.", "/settings/account", "test");
            var result = await sender.SendAsync(target, payload, vapid, subject, ct);
            if (result == PushDelivery.Gone) await db.PushSubscriptions.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
            return Results.Ok(result switch
            {
                PushDelivery.Delivered => new PushTestResult(true, "Test notification sent"),
                PushDelivery.Gone => new PushTestResult(false, "This device is no longer subscribed and was removed. Turn notifications on again on it."),
                _ => new PushTestResult(false, "The push service didn't accept the notification. Check the logs for details."),
            });
        });

        return app;
    }

    private static string? Validate(AddPushDeviceRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Endpoint) || req.Endpoint.Length > PushSubscriptionLimits.Endpoint || !WebPushSender.IsAllowedEndpoint(req.Endpoint))
            return "This browser's push service isn't supported.";
        if (!IsKey(req.P256dh, 65) || !IsKey(req.Auth, 16)) return "The browser sent invalid push keys.";
        return null;
    }

    private static bool IsKey(string? value, int length)
    {
        if (string.IsNullOrEmpty(value) || value.Length > PushSubscriptionLimits.Key) return false;
        try
        {
            return WebEncoders.Base64UrlDecode(value).Length == length;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static IResult NoUser() =>
        Results.Problem("Push notifications are for user accounts, not the API key.", statusCode: StatusCodes.Status400BadRequest);

    private static PushDeviceDto Dto(PushSubscription s) => new(s.Id, s.Name, s.Endpoint, List(s.Topics), s.CreatedAt);

    private static PushTopics Combine(IEnumerable<PushTopics> topics) => topics.Aggregate(PushTopics.None, (all, t) => all | t) & PushTopics.All;

    private static List<PushTopics> List(PushTopics topics) =>
        Enum.GetValues<PushTopics>().Where(t => t is not PushTopics.None and not PushTopics.All && topics.HasFlag(t)).ToList();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
