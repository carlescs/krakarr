using Krakarr.Core.Domain;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Krakarr.Data;

public class KrakarrDbContext(DbContextOptions<KrakarrDbContext> options) : DbContext(options)
{
    public const string SqliteProviderName = "Microsoft.EntityFrameworkCore.Sqlite";

    private static readonly JsonSerializerOptions JsonColumn = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public DbSet<User> Users => Set<User>();
    public DbSet<SettingEntry> Settings => Set<SettingEntry>();
    public DbSet<RootFolder> RootFolders => Set<RootFolder>();
    public DbSet<QualityProfile> QualityProfiles => Set<QualityProfile>();
    public DbSet<Indexer> Indexers => Set<Indexer>();
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<MovieFile> MovieFiles => Set<MovieFile>();
    public DbSet<MovieCollection> MovieCollections => Set<MovieCollection>();
    public DbSet<Series> Series => Set<Series>();
    public DbSet<Season> Seasons => Set<Season>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<EpisodeFile> EpisodeFiles => Set<EpisodeFile>();
    public DbSet<SubtitleFile> SubtitleFiles => Set<SubtitleFile>();
    public DbSet<Download> Downloads => Set<Download>();
    public DbSet<HistoryEntry> History => Set<HistoryEntry>();
    public DbSet<MediaRequest> MediaRequests => Set<MediaRequest>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        if (Database.ProviderName == SqliteProviderName)
        {
            // SQLite cannot order/compare DateTimeOffset natively; store as sortable binary ticks.
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
            configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        }
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Username).HasMaxLength(100);
            e.Property(x => x.SecurityStamp).HasMaxLength(64);
        });

        b.Entity<SettingEntry>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
        });

        b.Entity<RootFolder>(e => e.HasIndex(x => x.Path).IsUnique());

        b.Entity<Indexer>(e => e.HasIndex(x => x.Name).IsUnique());

        b.Entity<Movie>(e =>
        {
            e.HasIndex(x => x.TmdbId).IsUnique();
            e.HasIndex(x => x.ImdbId);
            e.HasOne(x => x.QualityProfile).WithMany().HasForeignKey(x => x.QualityProfileId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.MovieFile).WithOne().HasForeignKey<Movie>(x => x.MovieFileId).OnDelete(DeleteBehavior.SetNull);
            // Stored as a JSON text column: read as a whole, never queried into.
            e.Property(x => x.ReleaseDates)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, JsonColumn),
                    v => JsonSerializer.Deserialize<List<MovieReleaseDate>>(v, JsonColumn) ?? new List<MovieReleaseDate>(),
                    new ValueComparer<List<MovieReleaseDate>>(
                        (a, b) => (a ?? new List<MovieReleaseDate>()).SequenceEqual(b ?? new List<MovieReleaseDate>()),
                        v => v.Aggregate(0, (h, d) => HashCode.Combine(h, d.GetHashCode())),
                        v => v.ToList()))
                .HasDefaultValue(new List<MovieReleaseDate>());
            e.HasIndex(x => x.InCinemas);
            e.HasIndex(x => x.DigitalRelease);
            e.HasIndex(x => x.PhysicalRelease);
            e.HasOne(x => x.Collection).WithMany().HasForeignKey(x => x.CollectionId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.CollectionId);
        });

        b.Entity<MovieCollection>(e => e.HasIndex(x => x.TmdbId).IsUnique());

        b.Entity<MovieFile>(e => e.HasIndex(x => x.MovieId));

        b.Entity<Series>(e =>
        {
            e.HasIndex(x => x.TmdbId).IsUnique();
            e.HasIndex(x => x.TvdbId);
            e.HasOne(x => x.QualityProfile).WithMany().HasForeignKey(x => x.QualityProfileId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Seasons).WithOne().HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Episodes).WithOne().HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Season>(e => e.HasIndex(x => new { x.SeriesId, x.SeasonNumber }).IsUnique());

        b.Entity<Episode>(e =>
        {
            e.HasIndex(x => new { x.SeriesId, x.SeasonNumber, x.EpisodeNumber });
            e.HasIndex(x => x.AirDate);
            e.HasIndex(x => x.TmdbId);
            e.HasIndex(x => x.TvdbId);
            e.HasOne(x => x.EpisodeFile).WithMany().HasForeignKey(x => x.EpisodeFileId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<EpisodeFile>(e =>
        {
            e.HasIndex(x => x.SeriesId);
            e.HasOne<Series>().WithMany().HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SubtitleFile>(e =>
        {
            e.Property(x => x.Language).HasMaxLength(16);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.HasOne<MovieFile>().WithMany().HasForeignKey(x => x.MovieFileId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<EpisodeFile>().WithMany().HasForeignKey(x => x.EpisodeFileId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.MovieFileId);
            e.HasIndex(x => x.EpisodeFileId);
        });

        b.Entity<Download>(e =>
        {
            e.HasIndex(x => x.InfoHash).IsUnique();
            e.HasIndex(x => x.MovieId);
            e.HasIndex(x => x.SeriesId);
            e.Property(x => x.InfoHash).HasMaxLength(64);
            e.Property(x => x.Client).HasMaxLength(32).HasDefaultValue(DownloadClients.Embedded);
        });

        b.Entity<HistoryEntry>(e =>
        {
            e.HasIndex(x => x.Date);
            e.HasIndex(x => x.MovieId);
        });

        b.Entity<MediaRequest>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.PosterUrl).HasMaxLength(500);
            e.Property(x => x.DeclineReason).HasMaxLength(500);
            e.HasIndex(x => new { x.MediaType, x.TmdbId });
            e.HasIndex(x => new { x.Status, x.RequestedAt });
            e.HasIndex(x => x.RequestedById);
            // Deleting a user deletes their requests; deleting whoever processed one keeps it.
            e.HasOne(x => x.RequestedBy).WithMany().HasForeignKey(x => x.RequestedById).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ProcessedBy).WithMany().HasForeignKey(x => x.ProcessedById).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
