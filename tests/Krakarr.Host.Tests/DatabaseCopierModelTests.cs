using Krakarr.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Krakarr.Host.Tests;

/// <summary>
/// The copier reads everything from the EF model, so entities added later need no change to it. This model uses the
/// mappings Krakarr doesn't have yet (owned types, join tables, shadow keys, cycles, inheritance) to keep it that way.
/// </summary>
public sealed class DatabaseCopierModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("krakarr-copier").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        NpgsqlConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ShapesContext Create(string name) =>
        new(new DbContextOptionsBuilder<ShapesContext>().UseSqlite($"Data Source={Path.Combine(_dir, name)}").Options);

    [Fact]
    public async Task Copies_owned_types_join_tables_shadow_keys_cycles_and_inheritance()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(Create("source.db"), ct);
        await CopyAsync(Create("source.db"), Create("target.db"), ct);
        await AssertCopiedAsync(Create("target.db"), ct);
    }

    [Fact]
    public async Task Copies_the_same_model_into_postgres_and_back()
    {
        // Needs a PostgreSQL server: KRAKARR_TEST_POSTGRES is a connection string for a user that may create databases.
        if (Environment.GetEnvironmentVariable("KRAKARR_TEST_POSTGRES") is not { Length: > 0 } server)
        {
            Assert.Skip("Set KRAKARR_TEST_POSTGRES to run against PostgreSQL");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var connection = new NpgsqlConnectionStringBuilder(server) { Database = "krakarr_test_" + Guid.NewGuid().ToString("N")[..12] }.ConnectionString;
        ShapesContext Postgres() => new(new DbContextOptionsBuilder<ShapesContext>().UseNpgsql(connection).Options);
        try
        {
            await SeedAsync(Create("source.db"), ct);
            await CopyAsync(Create("source.db"), Postgres(), ct);
            await AssertCopiedAsync(Postgres(), ct);
            await CopyAsync(Postgres(), Create("back.db"), ct);
            await using var back = Create("back.db");
            Assert.Equal(3, await back.Authors.CountAsync(ct));
        }
        finally
        {
            await using var cleanup = Postgres();
            await cleanup.Database.EnsureDeletedAsync(CancellationToken.None);
        }
    }

    private static async Task SeedAsync(ShapesContext db, CancellationToken ct)
    {
        await using var _ = db;
        await db.Database.EnsureCreatedAsync(ct);
        var mentor = new Author
        {
            Name = "Mentor",
            Address = new Address { City = "Barcelona" },
            Contact = new Contact { Email = "mentor@example.org" },
            Links = [new Link { Url = "https://a.example" }, new Link { Url = "https://b.example" }],
        };
        db.Authors.Add(mentor);
        await db.SaveChangesAsync(ct);

        var pupil = new Author { Name = "Pupil", MentorId = mentor.Id, Contact = new Contact { Email = "pupil@example.org" } };
        db.Authors.Add(pupil);
        var fiction = new Tag { Name = "Fiction" };
        var classic = new Tag { Name = "Classic" };
        var book = new Book { Title = "Paper", Tags = [fiction, classic] };
        var ebook = new EBook { Title = "Digital", Format = "epub", Tags = [fiction] };
        db.AddRange(book, ebook);
        db.Entry(book).Property("AuthorId").CurrentValue = mentor.Id;
        db.Entry(ebook).Property("AuthorId").CurrentValue = mentor.Id;
        await db.SaveChangesAsync(ct);

        mentor.FavoriteBookId = ebook.Id;
        await db.SaveChangesAsync(ct);
    }

    private static async Task CopyAsync(ShapesContext from, ShapesContext to, CancellationToken ct)
    {
        await using var _ = from;
        await using var __ = to;
        await to.Database.EnsureCreatedAsync(ct);
        Assert.True(await DatabaseCopier.IsEmptyAsync(to, ct));
        var tables = await DatabaseCopier.CopyAsync(from, to, ct: ct);
        Assert.False(await DatabaseCopier.IsEmptyAsync(to, ct));
        Assert.Contains(tables, t => t.Table == "BookTag" && t.Rows == 3);
    }

    private static async Task AssertCopiedAsync(ShapesContext check, CancellationToken ct)
    {
        await using var _ = check;
        var authors = await check.Authors.AsNoTracking().OrderBy(a => a.Id).ToListAsync(ct);
        Assert.Equal(["Mentor", "Pupil"], authors.Select(a => a.Name));
        Assert.Equal("Barcelona", authors[0].Address?.City);
        Assert.Null(authors[1].Address);
        Assert.Equal("pupil@example.org", authors[1].Contact.Email);
        Assert.Equal(["https://a.example", "https://b.example"], authors[0].Links.OrderBy(l => l.Url).Select(l => l.Url));
        Assert.Equal(authors[0].Id, authors[1].MentorId);

        var digital = await check.Books.OfType<EBook>().Include(b => b.Tags).SingleAsync(ct);
        Assert.Equal("epub", digital.Format);
        Assert.Equal(digital.Id, authors[0].FavoriteBookId);
        Assert.Equal(authors[0].Id, check.Entry(digital).Property<int>("AuthorId").CurrentValue);
        Assert.Equal(["Fiction"], digital.Tags.Select(t => t.Name));
        Assert.Equal(2, (await check.Books.Include(b => b.Tags).SingleAsync(b => b.Title == "Paper", ct)).Tags.Count);

        // Generated ids carry on after the copied ones, owned collections included.
        var next = new Author { Name = "Next", Contact = new Contact { Email = "next@example.org" }, Links = [new Link { Url = "https://c.example" }] };
        check.Authors.Add(next);
        await check.SaveChangesAsync(ct);
        Assert.True(next.Id > authors[1].Id);
    }

    [Fact]
    public void Orders_tables_and_defers_only_the_keys_that_close_a_cycle()
    {
        using var db = Create("order.db");
        var order = DatabaseCopier.TablesInCopyOrder(db.Model).Select(t => t.GetTableName()).ToList();
        Assert.Equal(["Authors", "BookTag", "Books", "Tags"], order.Order(StringComparer.Ordinal));
        Assert.True(order.IndexOf("Authors") < order.IndexOf("Books"));
        Assert.True(order.IndexOf("Books") < order.IndexOf("BookTag"));
        Assert.True(order.IndexOf("Tags") < order.IndexOf("BookTag"));
        Assert.DoesNotContain("Links", order);
    }

    [Fact]
    public void Rejects_a_cycle_of_required_keys()
    {
        var options = new DbContextOptionsBuilder<RequiredCycleContext>().UseSqlite("Data Source=:memory:").Options;
        using var db = new RequiredCycleContext(options);
        var error = Assert.Throws<InvalidOperationException>(() => DatabaseCopier.TablesInCopyOrder(db.Model));
        Assert.Contains("cycle", error.Message);
    }

    private sealed class ShapesContext(DbContextOptions<ShapesContext> options) : DbContext(options)
    {
        public DbSet<Author> Authors => Set<Author>();
        public DbSet<Book> Books => Set<Book>();
        public DbSet<Tag> Tags => Set<Tag>();

        protected override void OnModelCreating(ModelBuilder b)
        {
            b.Entity<Author>(e =>
            {
                e.OwnsOne(x => x.Address);
                e.ComplexProperty(x => x.Contact);
                // An owned collection in its own table, keyed by a shadow id.
                e.OwnsMany(x => x.Links, l =>
                {
                    l.ToTable("Links");
                    l.Property<int>("Id");
                    l.HasKey("Id");
                });
                // A self-reference, and a reference to a table that references this one: both close a cycle.
                e.HasOne<Author>().WithMany().HasForeignKey(x => x.MentorId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne<Book>().WithMany().HasForeignKey(x => x.FavoriteBookId).OnDelete(DeleteBehavior.Restrict);
            });

            b.Entity<Book>(e =>
            {
                e.HasDiscriminator<string>("Kind").HasValue<Book>("paper").HasValue<EBook>("digital");
                // A foreign key with no property on the class.
                e.HasOne<Author>().WithMany().HasForeignKey("AuthorId").IsRequired();
                // A many-to-many relationship: EF maps the join table as a shared-type entity.
                e.HasMany(x => x.Tags).WithMany();
            });
        }
    }

    private sealed class RequiredCycleContext(DbContextOptions<RequiredCycleContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            b.Entity<Left>().HasOne<Right>().WithMany().HasForeignKey(x => x.RightId).OnDelete(DeleteBehavior.Restrict);
            b.Entity<Right>().HasOne<Left>().WithMany().HasForeignKey(x => x.LeftId).OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class Author
    {
        public int Id { get; set; }
        public required string Name { get; set; }
        public int? MentorId { get; set; }
        public int? FavoriteBookId { get; set; }
        public Address? Address { get; set; }
        public required Contact Contact { get; set; }
        public List<Link> Links { get; set; } = [];
    }

    private sealed class Address
    {
        public required string City { get; set; }
    }

    private sealed class Contact
    {
        public required string Email { get; set; }
    }

    private sealed class Link
    {
        public required string Url { get; set; }
    }

    private class Book
    {
        public int Id { get; set; }
        public required string Title { get; set; }
        public List<Tag> Tags { get; set; } = [];
    }

    private sealed class EBook : Book
    {
        public required string Format { get; set; }
    }

    private sealed class Tag
    {
        public int Id { get; set; }
        public required string Name { get; set; }
    }

    private sealed class Left
    {
        public int Id { get; set; }
        public int RightId { get; set; }
    }

    private sealed class Right
    {
        public int Id { get; set; }
        public int LeftId { get; set; }
    }
}
