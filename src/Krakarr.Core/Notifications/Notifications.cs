using System.Text.Json.Serialization;
using Krakarr.Core.Domain;

namespace Krakarr.Core.Notifications;

public enum NotificationSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// Something the signed-in user should look at, shown in the bell in the top bar. Notifications are computed from
/// the current state each time (e.g. "3 requests waiting for approval"), so they disappear once dealt with.
/// </summary>
/// <param name="Id">Stable id, unique across sources (e.g. "requests.pending"); lets the UI keep track of an item.</param>
/// <param name="Kind">What it is about, for the UI's icon (e.g. "request").</param>
/// <param name="Link">SPA route the notification opens, e.g. "/requests".</param>
/// <param name="Count">How many things it stands for; the bell shows the sum.</param>
/// <param name="Date">When the latest of them happened, if known.</param>
/// <param name="CountRenotifies">Whether a higher <paramref name="Count"/> on the same <paramref name="Date"/> brings a
/// dismissed notification back. False when the source moves <paramref name="Date"/> whenever it should come back (health
/// problems that notify at most once a day).</param>
public sealed record Notification(
    string Id,
    string Kind,
    string Title,
    string? Message,
    string Link,
    int Count = 1,
    NotificationSeverity Severity = NotificationSeverity.Info,
    DateTimeOffset? Date = null,
    [property: JsonIgnore] bool CountRenotifies = true);

/// <summary>Who is asking: sources must only return what this user may see.</summary>
public sealed record NotificationContext(int? UserId, UserRole Role)
{
    public bool IsStaff => Role is UserRole.Admin or UserRole.Manager;
}

/// <summary>
/// A module's contribution to the notification bell. Register implementations as scoped services with
/// <c>services.AddScoped&lt;INotificationSource, ...&gt;()</c>; <c>GET /api/v1/notifications</c> asks every source.
/// Sources are called on each bell refresh, so keep them to cheap aggregate queries (counts, not rows), and filter by
/// <see cref="NotificationContext.Role"/> since the endpoint is open to every user. Publish
/// <c>EventNames.NotificationsUpdated</c> when what a source reports changes so open UIs refresh.
/// </summary>
public interface INotificationSource
{
    Task<IReadOnlyList<Notification>> GetAsync(NotificationContext context, CancellationToken ct = default);
}
