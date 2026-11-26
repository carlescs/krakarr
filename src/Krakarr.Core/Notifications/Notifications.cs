using Krakarr.Core.Domain;

namespace Krakarr.Core.Notifications;

public enum NotificationSeverity
{
    /// <summary>Something finished well (shown in green). Below <see cref="Info"/> so it sorts last.</summary>
    Success = -1,
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
/// <param name="Progress">Set for work in progress (e.g. a subtitle sync): the bell shows a progress bar. Live updates
/// arrive as <c>EventNames.NotificationProgress</c> events so clients don't refetch every source each time.</param>
/// <param name="Actions">Buttons shown on the notification (e.g. cancel a running sync).</param>
/// <param name="Tray">Which icon in the top bar lists it (<see cref="NotificationTrays"/>); the bell by default.</param>
public sealed record Notification(
    string Id,
    string Kind,
    string Title,
    string? Message,
    string Link,
    int Count = 1,
    NotificationSeverity Severity = NotificationSeverity.Info,
    DateTimeOffset? Date = null,
    NotificationProgress? Progress = null,
    IReadOnlyList<NotificationAction>? Actions = null,
    string Tray = NotificationTrays.Bell);

/// <summary>The icons in the top bar that list notifications. Each has its own badge, panel and "Dismiss all".</summary>
public static class NotificationTrays
{
    /// <summary>The bell: everything that has no tray of its own.</summary>
    public const string Bell = "bell";

    /// <summary>Subtitle syncs waiting, running or just finished.</summary>
    public const string SubtitleSync = "subtitleSync";
}

/// <summary>A button on a notification. The bell POSTs to <paramref name="Path"/>, which is relative to <c>/api/v1</c>.</summary>
public sealed record NotificationAction(string Label, string Path);

/// <summary>How far along a notification's work is.</summary>
/// <param name="Percent">0 to 100, or null when the work can't tell (the bar is indeterminate).</param>
/// <param name="RemainingSeconds">Estimated time left, or null when there is no estimate yet.</param>
public sealed record NotificationProgress(double? Percent, double? RemainingSeconds = null);

/// <summary>Payload of <c>EventNames.NotificationProgress</c>: new progress and message for the notification <paramref name="Id"/>.</summary>
public sealed record NotificationProgressUpdate(string Id, double? Percent, string? Message, double? RemainingSeconds = null);

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
