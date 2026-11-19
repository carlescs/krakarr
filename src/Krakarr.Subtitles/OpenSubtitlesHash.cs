namespace Krakarr.Subtitles;

/// <summary>
/// The OpenSubtitles "moviehash": file size plus the 64-bit little-endian sums of the first and last 64 KiB,
/// with unsigned overflow. Providers use it to find subtitles synced to exactly this file.
/// </summary>
public static class OpenSubtitlesHash
{
    private const int ChunkSize = 64 * 1024;

    public static string Compute(string path)
    {
        using var stream = File.OpenRead(path);
        return Compute(stream);
    }

    public static string Compute(Stream stream)
    {
        var length = stream.Length;
        ulong hash = (ulong)length;
        var buffer = new byte[ChunkSize];

        hash = unchecked(hash + SumChunk(stream, 0, buffer));
        hash = unchecked(hash + SumChunk(stream, Math.Max(0, length - ChunkSize), buffer));
        return hash.ToString("x16");
    }

    private static ulong SumChunk(Stream stream, long offset, byte[] buffer)
    {
        Array.Clear(buffer);
        stream.Seek(offset, SeekOrigin.Begin);
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }

        ulong sum = 0;
        for (var i = 0; i + 8 <= buffer.Length; i += 8)
        {
            sum = unchecked(sum + BitConverter.ToUInt64(buffer, i));
        }

        return sum;
    }
}
