using System.Security.Cryptography;
using System.Text;
using Krakarr.Core.Configuration;

namespace Krakarr.Host.Auth;

/// <summary>
/// One-time token that must accompany the first-run setup request, so only someone with access to the server's log or
/// config folder can create the administrator account (protects fresh instances against drive-by and DNS rebinding
/// takeovers). Generated at startup while no user exists, or preset with <c>Krakarr:SetupToken</c>.
/// </summary>
public sealed class SetupTokenStore(KrakarrPaths paths, IConfiguration configuration, ILogger<SetupTokenStore> logger)
{
    public const string FileName = "setup-token";

    private string? _token;

    /// <summary>Serializes setup requests so two concurrent calls cannot both create an administrator.</summary>
    public SemaphoreSlim Lock { get; } = new(1, 1);

    public string FilePath => Path.Combine(paths.ConfigDirectory, FileName);

    public void Initialize(bool setupRequired)
    {
        if (!setupRequired)
        {
            Consume();
            return;
        }

        var configured = configuration["Krakarr:SetupToken"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _token = configured.Trim();
            logger.LogWarning("No user exists yet. Finish setup in the web UI using the configured setup token (Krakarr__SetupToken)");
            return;
        }

        _token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        try
        {
            File.WriteAllText(FilePath, _token + Environment.NewLine);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write the setup token to {Path}", FilePath);
        }

        logger.LogWarning(
            "No user exists yet. To create the administrator account, open the web UI and enter this setup token: {SetupToken} (also saved to {Path})",
            _token, FilePath);
    }

    public bool Validate(string? provided) =>
        _token is { } expected
        && !string.IsNullOrEmpty(provided)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided.Trim()), Encoding.UTF8.GetBytes(expected));

    /// <summary>Invalidates the token once setup is done.</summary>
    public void Consume()
    {
        _token = null;
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete {Path}", FilePath);
        }
    }
}
