namespace Krakarr.Data;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>"sqlite" (default) or "postgres".</summary>
    public string Provider { get; set; } = "sqlite";

    /// <summary>Connection string; for SQLite defaults to {config}/krakarr.db.</summary>
    public string? ConnectionString { get; set; }
}
