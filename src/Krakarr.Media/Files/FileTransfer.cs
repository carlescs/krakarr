using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Krakarr.Media.Files;

public enum TransferMode
{
    HardLink,
    Copy,
    Move,
}

public interface IFileTransfer
{
    /// <summary>Transfers <paramref name="source"/> to <paramref name="destination"/>, returning the mode actually used.</summary>
    TransferMode Transfer(string source, string destination, TransferMode preferred, bool overwrite = true);
}

/// <summary>Hardlinks when possible (same filesystem), falling back to copy.</summary>
public sealed partial class FileTransfer(ILogger<FileTransfer> logger) : IFileTransfer
{
    public TransferMode Transfer(string source, string destination, TransferMode preferred, bool overwrite = true)
    {
        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(destination))
        {
            if (!overwrite)
            {
                throw new IOException($"Destination already exists: {destination}");
            }

            File.Delete(destination);
        }

        if (preferred == TransferMode.Move)
        {
            File.Move(source, destination);
            return TransferMode.Move;
        }

        if (preferred == TransferMode.HardLink && TryHardLink(source, destination))
        {
            return TransferMode.HardLink;
        }

        if (preferred == TransferMode.HardLink)
        {
            logger.LogInformation("Hardlink not possible for {Source}, copying instead", source);
        }

        File.Copy(source, destination, overwrite);
        return TransferMode.Copy;
    }

    public static bool TryHardLink(string source, string destination)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return CreateHardLinkW(destination, source, IntPtr.Zero);
            }

            return link(source, destination) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int link(string oldpath, string newpath);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
