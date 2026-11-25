namespace Krakarr.Core.MediaServers;

/// <summary>
/// Tells the configured media servers (Jellyfin, Emby, Plex) that a library folder changed, so they rescan it right away
/// instead of waiting for their own scheduled scan. Calls only queue the folder: refreshes are grouped and sent in the
/// background, so callers never wait on (or fail because of) a media server.
/// </summary>
public interface IMediaServerNotifier
{
    /// <summary>Queues a refresh of <paramref name="path"/>, a folder (or a file, whose folder is refreshed) under a library root.</summary>
    void PathChanged(string path);
}

/// <summary>Does nothing; used when no media server integration is registered (tests, tools).</summary>
public sealed class NullMediaServerNotifier : IMediaServerNotifier
{
    public void PathChanged(string path) { }
}
