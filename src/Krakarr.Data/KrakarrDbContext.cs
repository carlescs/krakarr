using Krakarr.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Krakarr.Data;

public class KrakarrDbContext(DbContextOptions<KrakarrDbContext> options) : DbContext(options)
{
    public const string SqliteProviderName = "Microsoft.EntityFrameworkCore.Sqlite";

    public DbSet<User> Users => Set<User>();
    public DbSet<SettingEntry> Settings => Set<SettingEntry>();
    public DbSet<RootFolder> RootFolders => Set<RootFolder>();
    public DbSet<QualityProfile> QualityProfiles => Set<QualityProfile>();
    public DbSet<Indexer> Indexers => Set<Indexer>();
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<MovieFile> MovieFiles => Set<MovieFile>();
    public DbSet<Series> Series => Set<Series>();
    public DbSet<Season> Seasons => Set<Season>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<EpisodeFile> EpisodeFiles => Set<EpisodeFile>();
    public DbSet<SubtitleFile> SubtitleFiles => Set<SubtitleFile>();
    public DbSet<Download> Downloads => Set<Download>();
    public DbSet<HistoryEntry> History => Set<HistoryEntry>();

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
        });

        b.Entity<MovieFile>(e => e.HasIndex(x => x.MovieId));

        b.Entity<Series>(e =>
        {
            e.HasIndex(x => x.TvdbId).IsUnique();
            e.HasOne(x => x.QualityProfile).WithMany().HasForeignKey(x => x.QualityProfileId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Seasons).WithOne().HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Episodes).WithOne().HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Season>(e => e.HasIndex(x => new { x.SeriesId, x.SeasonNumber }).IsUnique());

        b.Entity<Episode>(e =>
        {
            e.HasIndex(x => new { x.SeriesId, x.SeasonNumber, x.EpisodeNumber });
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
    }
}
