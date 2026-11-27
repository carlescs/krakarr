namespace Krakarr.Core.Push;

/// <summary>
/// What a device wants push notifications about. Each device (browser or installed app) picks its own topics; topics a
/// user's role doesn't cover are ignored when sending.
/// </summary>
[Flags]
public enum PushTopics
{
    None = 0,
    /// <summary>A title the user follows was imported (now available to watch). Everyone.</summary>
    Available = 1,
    /// <summary>Any download was imported. Staff.</summary>
    Imported = 2,
    /// <summary>A download failed to import. Staff.</summary>
    Failed = 4,
    /// <summary>Someone requested a title. Staff.</summary>
    Requests = 8,
    /// <summary>The user's own request was approved or declined. Everyone.</summary>
    RequestResults = 16,
    /// <summary>A health check started failing with an error. Admins.</summary>
    Health = 32,

    All = Available | Imported | Failed | Requests | RequestResults | Health,
}

/// <summary>
/// The event behind a push notification; who receives it follows from the kind and the ids. Text stays short: push
/// services cap the encrypted payload at about 4 KB.
/// </summary>
/// <param name="Title">First line of the notification, e.g. the movie's title and year.</param>
/// <param name="Body">Second line, e.g. "Imported in Bluray-1080p".</param>
/// <param name="Link">SPA route opened when the notification is tapped, e.g. "/movies/12".</param>
/// <param name="Tag">Replaces an earlier notification with the same tag on the device instead of stacking up.</param>
/// <param name="MovieId">The library movie it is about (followers get <see cref="PushTopics.Available"/>).</param>
/// <param name="SeriesId">The library series it is about.</param>
/// <param name="UserId">The only user it is for (<see cref="PushKind.RequestResult"/>).</param>
public sealed record PushMessage(
    PushKind Kind,
    string Title,
    string? Body,
    string Link,
    string? Tag = null,
    int? MovieId = null,
    int? SeriesId = null,
    int? UserId = null);

public enum PushKind
{
    /// <summary>Staff with <see cref="PushTopics.Imported"/>, and followers of the title with <see cref="PushTopics.Available"/>.</summary>
    Imported,
    /// <summary>Staff with <see cref="PushTopics.Failed"/>.</summary>
    ImportFailed,
    /// <summary>Staff with <see cref="PushTopics.Requests"/>.</summary>
    RequestCreated,
    /// <summary><see cref="PushMessage.UserId"/> with <see cref="PushTopics.RequestResults"/>.</summary>
    RequestResult,
    /// <summary>Admins with <see cref="PushTopics.Health"/>.</summary>
    Health,
}

/// <summary>
/// Sends push notifications to users' phones and browsers. <see cref="Send"/> only queues the message and never
/// throws or waits, so callers (imports, request endpoints) are never slowed down or broken by a push service.
/// </summary>
public interface IPushNotifier
{
    void Send(PushMessage message);
}

/// <summary>Used when push isn't wired up (tests, tools).</summary>
public sealed class NullPushNotifier : IPushNotifier
{
    public static readonly NullPushNotifier Instance = new();

    public void Send(PushMessage message) { }
}
