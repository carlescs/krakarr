using System.IO.Compression;
using System.Text.Json;

namespace Krakarr.Host.Backups;

/// <summary>What <c>backup.json</c> in a backup says about it.</summary>
public sealed record BackupManifest(int FormatVersion, string AppVersion, string Provider, DateTimeOffset Created, string Type);

/// <summary>
/// Layout of a backup zip, and the checks a zip must pass before it is restored. Only the entries listed here are
/// accepted, so a crafted archive can't write anywhere else (no paths are taken from the zip).
/// </summary>
public static class BackupArchive
{
    public const int FormatVersion = 1;
    public const string ManifestEntry = "backup.json";
    /// <summary>SQLite only: a consistent copy of the database.</summary>
    public const string DatabaseEntry = "krakarr.db";
    /// <summary>PostgreSQL only: the settings table (back up the rest of the database with <c>pg_dump</c>).</summary>
    public const string SettingsEntry = "settings.json";
    public const string ConfigEntry = "config.json";
    public const string KeysPrefix = "keys/";

    private const long MaxManifestBytes = 64 * 1024;
    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A data-protection key file name: <c>keys/key-&lt;guid&gt;.xml</c> style, one segment, no traversal.</summary>
    public static bool IsKeyEntry(string name) =>
        name.StartsWith(KeysPrefix, StringComparison.Ordinal)
        && name.Length > KeysPrefix.Length
        && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
        && name.AsSpan(KeysPrefix.Length).IndexOfAny('/', '\\') < 0
        && !name.Contains("..", StringComparison.Ordinal);

    /// <summary>Checks that <paramref name="path"/> is a Krakarr backup that can be restored on <paramref name="provider"/>.</summary>
    /// <returns>The manifest, or an error message for the user.</returns>
    public static (BackupManifest? Manifest, string? Error) Validate(string path, string provider)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return Validate(zip, provider);
        }
        catch (InvalidDataException)
        {
            return (null, "The file is not a valid zip archive");
        }
    }

    public static (BackupManifest? Manifest, string? Error) Validate(ZipArchive zip, string provider)
    {
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (name is ManifestEntry or DatabaseEntry or SettingsEntry or ConfigEntry || IsKeyEntry(name) || name == KeysPrefix) continue;
            return (null, $"Unexpected file in the backup: {name}");
        }

        var manifestEntry = zip.GetEntry(ManifestEntry);
        if (manifestEntry is null || manifestEntry.Length > MaxManifestBytes) return (null, "Not a Krakarr backup (backup.json is missing)");
        BackupManifest? manifest;
        try
        {
            using var stream = manifestEntry.Open();
            manifest = JsonSerializer.Deserialize<BackupManifest>(stream, Json);
        }
        catch (JsonException)
        {
            manifest = null;
        }

        if (manifest is null || string.IsNullOrEmpty(manifest.Provider)) return (null, "Not a Krakarr backup (backup.json is invalid)");
        if (manifest.FormatVersion > FormatVersion) return (null, $"The backup was made by a newer Krakarr ({manifest.AppVersion}); update Krakarr first");
        if (!string.Equals(manifest.Provider, provider, StringComparison.OrdinalIgnoreCase))
            return (null, $"The backup is of a {manifest.Provider} database, but Krakarr is using {provider}");

        if (provider == "sqlite")
        {
            var db = zip.GetEntry(DatabaseEntry);
            if (db is null) return (null, "The backup has no database");
            Span<byte> header = stackalloc byte[16];
            using var stream = db.Open();
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length || !header.SequenceEqual(SqliteHeader))
                return (null, "The database in the backup is not a SQLite database");
        }
        else if (zip.GetEntry(SettingsEntry) is null)
        {
            return (null, "The backup has no settings");
        }

        return (manifest, null);
    }
}
