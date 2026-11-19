using Krakarr.Media.Files;

namespace Krakarr.Media.Tests;

public class FileNameBuilderTests
{
    [Fact]
    public void Expands_tokens()
    {
        var result = FileNameBuilder.Build("{Movie Title} ({Release Year}) [{Quality}]", new Dictionary<string, string?>
        {
            ["Movie Title"] = "The Matrix",
            ["Release Year"] = "1999",
            ["Quality"] = "Bluray-1080p",
        });
        Assert.Equal("The Matrix (1999) [Bluray-1080p]", result);
    }

    [Fact]
    public void Removes_empty_brackets_and_invalid_characters()
    {
        var result = FileNameBuilder.Build("{Movie Title} ({Release Year}) [{Quality}]", new Dictionary<string, string?>
        {
            ["Movie Title"] = "Mission: Impossible / Fallout?",
            ["Release Year"] = null,
            ["Quality"] = null,
        });
        Assert.Equal("Mission- Impossible Fallout", result);
    }

    [Fact]
    public void Leaves_unknown_tokens()
    {
        Assert.Equal("{Nope}", FileNameBuilder.Build("{Nope}", new Dictionary<string, string?>()));
    }

    [Fact]
    public void Hardlinks_or_copies_files()
    {
        var dir = Directory.CreateTempSubdirectory("krakarr-test");
        try
        {
            var source = Path.Combine(dir.FullName, "src.mkv");
            File.WriteAllText(source, "data");
            var transfer = new FileTransfer(Microsoft.Extensions.Logging.Abstractions.NullLogger<FileTransfer>.Instance);
            var dest = Path.Combine(dir.FullName, "lib", "Movie (2020)", "Movie (2020).mkv");
            var mode = transfer.Transfer(source, dest, TransferMode.HardLink);
            Assert.True(File.Exists(dest));
            Assert.Equal("data", File.ReadAllText(dest));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(TransferMode.HardLink, mode);
            }
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
