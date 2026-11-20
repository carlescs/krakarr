using Krakarr.Core.Domain;
using Krakarr.Core.Notifications;
using Krakarr.Data;
using Krakarr.Host.Auth;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Endpoints;

public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        // Everything the caller should look at, from every registered source, most severe and most recent first.
        app.MapGet("/api/v1/notifications", async (HttpContext http, IEnumerable<INotificationSource> sources, CancellationToken ct) =>
        {
            var context = new NotificationContext(http.User.GetUserId(), http.User.GetRole());
            var all = new List<Notification>();
            foreach (var source in sources)
            {
                all.AddRange(await source.GetAsync(context, ct));
            }

            return Results.Ok(all.Where(n => n.Count > 0)
                .OrderByDescending(n => n.Severity).ThenByDescending(n => n.Date ?? DateTimeOffset.MinValue).ToList());
        }).WithTags("Notifications");

        return app;
    }
}

/// <summary>Staff: requests waiting for approval. Users: their own requests that are still pending.</summary>
public sealed class RequestNotificationSource(KrakarrDbContext db) : INotificationSource
{
    public async Task<IReadOnlyList<Notification>> GetAsync(NotificationContext context, CancellationToken ct = default)
    {
        var pending = db.MediaRequests.AsNoTracking().Where(r => r.Status == RequestStatus.Pending);
        if (!context.IsStaff)
        {
            if (context.UserId is not { } userId) return [];
            pending = pending.Where(r => r.RequestedById == userId);
        }

        var summary = await pending.GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Latest = g.Max(r => r.RequestedAt) })
            .FirstOrDefaultAsync(ct);
        if (summary is null) return [];

        var n = summary.Count;
        return
        [
            context.IsStaff
                ? new Notification("requests.pending", "request", n == 1 ? "1 request waiting for approval" : $"{n} requests waiting for approval",
                    "Approve or decline them on the Requests page.", "/requests", n, Date: summary.Latest)
                : new Notification("requests.mine", "request", n == 1 ? "1 of your requests is pending" : $"{n} of your requests are pending",
                    "An admin or manager will review them.", "/requests", n, Date: summary.Latest),
        ];
    }
}
