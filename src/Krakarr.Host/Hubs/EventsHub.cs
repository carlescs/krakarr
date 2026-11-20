using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Host.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Krakarr.Host.Hubs;

[Authorize]
public sealed class EventsHub : Hub
{
    /// <summary>Admins and managers: receive download, command and history events.</summary>
    public const string StaffGroup = "staff";

    public override async Task OnConnectedAsync()
    {
        if (Context.User?.GetRole() is UserRole.Admin or UserRole.Manager)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, StaffGroup);
        }

        await base.OnConnectedAsync();
    }
}

public sealed class SignalREventHub(IHubContext<EventsHub> hub) : IEventHub
{
    /// <summary>Library changes every signed-in user may see; everything else (downloads, commands, history) is staff only.</summary>
    private static readonly HashSet<string> PublicEvents =
        [EventNames.MovieUpdated, EventNames.SeriesUpdated, EventNames.SubtitlesUpdated];

    public Task PublishAsync(string eventName, object? payload, CancellationToken cancellationToken = default) =>
        (PublicEvents.Contains(eventName) ? hub.Clients.All : hub.Clients.Group(EventsHub.StaffGroup))
            .SendAsync(eventName, payload, cancellationToken);
}
