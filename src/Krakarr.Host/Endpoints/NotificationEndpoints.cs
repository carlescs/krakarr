using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Core.Notifications;
using Krakarr.Data;
using Krakarr.Host.Auth;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Endpoints;

public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/notifications").WithTags("Notifications");

        // Everything the caller should look at, from every registered source, most severe and most recent first.
        group.MapGet("/", async (HttpContext http, KrakarrDbContext db, IEnumerable<INotificationSource> sources, CancellationToken ct) =>
        {
            var context = new NotificationContext(http.User.GetUserId(), http.User.GetRole());
            var all = await ActiveAsync(sources, context, ct);
            if (all.Count > 0 && context.UserId is { } userId)
            {
                var dismissed = await db.NotificationDismissals.AsNoTracking()
                    .Where(d => d.UserId == userId)
                    .ToDictionaryAsync(d => d.NotificationId, ct);
                all.RemoveAll(n => dismissed.TryGetValue(n.Id, out var d) && IsDismissed(n, d));
            }

            return Results.Ok(all
                .OrderByDescending(n => n.Severity).ThenByDescending(n => n.Date ?? DateTimeOffset.MinValue).ToList());
        });

        group.MapDelete("/", (HttpContext http, KrakarrDbContext db, IEnumerable<INotificationSource> sources, IEventHub events, CancellationToken ct) =>
            DismissAsync(http, db, sources, events, null, ct));

        group.MapDelete("/{id}", (string id, HttpContext http, KrakarrDbContext db, IEnumerable<INotificationSource> sources, IEventHub events, CancellationToken ct) =>
            DismissAsync(http, db, sources, events, id, ct));

        return app;
    }

    /// <summary>True when <paramref name="n"/> has no activity newer than what the user dismissed.</summary>
    public static bool IsDismissed(Notification n, NotificationDismissal d) =>
        n.Date is { } date
            ? d.DismissedAt is { } dismissedAt && (date < dismissedAt || (date == dismissedAt && n.Count <= d.Count))
            : d.DismissedAt is null && n.Count <= d.Count;

    private static async Task<List<Notification>> ActiveAsync(IEnumerable<INotificationSource> sources, NotificationContext context, CancellationToken ct)
    {
        var all = new List<Notification>();
        foreach (var source in sources)
        {
            all.AddRange(await source.GetAsync(context, ct));
        }

        all.RemoveAll(n => n.Count <= 0);
        return all;
    }

    private static async Task<IResult> DismissAsync(HttpContext http, KrakarrDbContext db, IEnumerable<INotificationSource> sources,
        IEventHub events, string? id, CancellationToken ct)
    {
        if (http.User.GetUserId() is not { } userId) return Results.NoContent();
        var changed = 0;

        // Result notifications come from MediaRequest.ResultUnseen; clearing that flag dismisses the handled requests
        // themselves so a later approval or decline reports only the new ones.
        if (id is null or "requests.approved")
        {
            changed += await db.MediaRequests
                .Where(r => r.RequestedById == userId && r.ResultUnseen && r.Status == RequestStatus.Approved)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ResultUnseen, false), ct);
        }

        if (id is null or "requests.declined")
        {
            changed += await db.MediaRequests
                .Where(r => r.RequestedById == userId && r.ResultUnseen && r.Status == RequestStatus.Declined)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ResultUnseen, false), ct);
        }

        var context = new NotificationContext(userId, http.User.GetRole());
        var target = (await ActiveAsync(sources, context, ct))
            .Where(n => id is null || n.Id == id)
            .ToList();
        if (target.Count > 0)
        {
            var existing = await db.NotificationDismissals
                .Where(d => d.UserId == userId)
                .ToDictionaryAsync(d => d.NotificationId, ct);
            foreach (var n in target)
            {
                if (existing.TryGetValue(n.Id, out var row))
                {
                    row.Count = n.Count;
                    row.DismissedAt = n.Date;
                }
                else
                {
                    db.NotificationDismissals.Add(new NotificationDismissal
                    {
                        UserId = userId,
                        NotificationId = n.Id,
                        Count = n.Count,
                        DismissedAt = n.Date,
                    });
                }

                changed++;
            }

            await db.SaveChangesAsync(ct);
        }

        if (changed > 0) await events.PublishAsync(EventNames.NotificationsUpdated, null, ct);
        return Results.NoContent();
    }
}

/// <summary>
/// Staff: requests waiting for approval. Users: their own requests that are still pending. Everyone: their own
/// requests that were approved or declined since they last opened the Requests page.
/// </summary>
public sealed class RequestNotificationSource(KrakarrDbContext db) : INotificationSource
{
    /// <summary>Titles named in a result notification's message; the rest are summed up as "and N more".</summary>
    internal const int NamedTitles = 3;

    public async Task<IReadOnlyList<Notification>> GetAsync(NotificationContext context, CancellationToken ct = default)
    {
        var result = new List<Notification>(3);
        if (await PendingAsync(context, ct) is { } pending) result.Add(pending);
        if (context.UserId is { } userId)
        {
            if (await ResultAsync(userId, RequestStatus.Approved, ct) is { } approved) result.Add(approved);
            if (await ResultAsync(userId, RequestStatus.Declined, ct) is { } declined) result.Add(declined);
        }

        return result;
    }

    private async Task<Notification?> PendingAsync(NotificationContext context, CancellationToken ct)
    {
        var pending = db.MediaRequests.AsNoTracking().Where(r => r.Status == RequestStatus.Pending);
        if (!context.IsStaff)
        {
            if (context.UserId is not { } userId) return null;
            pending = pending.Where(r => r.RequestedById == userId);
        }

        var summary = await pending.GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Latest = g.Max(r => r.RequestedAt) })
            .FirstOrDefaultAsync(ct);
        if (summary is null) return null;

        var n = summary.Count;
        return context.IsStaff
            ? new Notification("requests.pending", "request", n == 1 ? "1 request waiting for approval" : $"{n} requests waiting for approval",
                "Approve or decline them on the Requests page.", "/requests", n, Date: summary.Latest)
            : new Notification("requests.mine", "request", n == 1 ? "1 of your requests is pending" : $"{n} of your requests are pending",
                "An admin or manager will review them.", "/requests", n, Date: summary.Latest);
    }

    /// <summary>The caller's requests that ended up <paramref name="status"/> and that they haven't seen yet.</summary>
    private async Task<Notification?> ResultAsync(int userId, RequestStatus status, CancellationToken ct)
    {
        var unseen = db.MediaRequests.AsNoTracking().Where(r => r.RequestedById == userId && r.ResultUnseen && r.Status == status);
        var count = await unseen.CountAsync(ct);
        if (count == 0) return null;

        var latest = await unseen.OrderByDescending(r => r.ProcessedAt).ThenByDescending(r => r.Id)
            .Select(r => new { r.Title, r.ProcessedAt }).Take(NamedTitles).ToListAsync(ct);
        var titles = string.Join(", ", latest.Select(r => r.Title));
        var message = count > latest.Count ? $"{titles} and {count - latest.Count} more" : titles;

        return status == RequestStatus.Approved
            ? new Notification("requests.approved", "request", count == 1 ? "Your request was approved" : $"{count} of your requests were approved",
                message, "/requests", count, Date: latest[0].ProcessedAt)
            : new Notification("requests.declined", "request", count == 1 ? "Your request was declined" : $"{count} of your requests were declined",
                message, "/requests", count, Date: latest[0].ProcessedAt);
    }
}
