using Krakarr.Core.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Krakarr.Host.Hubs;

[Authorize]
public sealed class EventsHub : Hub;

public sealed class SignalREventHub(IHubContext<EventsHub> hub) : IEventHub
{
    public Task PublishAsync(string eventName, object? payload, CancellationToken cancellationToken = default) =>
        hub.Clients.All.SendAsync(eventName, payload, cancellationToken);
}
