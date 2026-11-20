using System.Buffers;

namespace Krakarr.Core.Http;

/// <summary>A response body was larger than the caller allows.</summary>
public sealed class ResponseTooLargeException(long limit)
    : HttpRequestException($"Response is larger than {limit / (1024 * 1024)} MB");

/// <summary>
/// Reads HTTP bodies with an upper bound, so a misbehaving indexer or tracker cannot make the process buffer an
/// arbitrarily large response in memory.
/// </summary>
public static class BoundedContent
{
    /// <summary>Upper bound for .torrent files (also used for uploads).</summary>
    public const long MaxTorrentBytes = 10 * 1024 * 1024;

    /// <summary>Upper bound for subtitle files.</summary>
    public const long MaxSubtitleBytes = 10 * 1024 * 1024;

    /// <summary>Upper bound for indexer XML (search results, capabilities).</summary>
    public const long MaxIndexerResponseBytes = 32 * 1024 * 1024;

    /// <summary>Reads the whole body into an exactly sized array, failing once it exceeds <paramref name="maxBytes"/>.</summary>
    public static async Task<byte[]> ReadAsByteArrayAsync(this HttpContent content, long maxBytes, CancellationToken ct = default)
    {
        var declared = content.Headers.ContentLength;
        if (declared > maxBytes) throw new ResponseTooLargeException(maxBytes);

        await using var stream = await content.ReadAsStreamAsync(ct);
        // Size the buffer from Content-Length when known so the common case never grows or copies it.
        using var buffer = new MemoryStream(declared is { } length ? (int)length : 0);
        var chunk = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > maxBytes) throw new ResponseTooLargeException(maxBytes);
                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.Length == buffer.Capacity ? buffer.GetBuffer() : buffer.ToArray();
    }

    /// <summary>Opens the body as a stream that throws <see cref="ResponseTooLargeException"/> past <paramref name="maxBytes"/>.</summary>
    public static async Task<Stream> ReadAsBoundedStreamAsync(this HttpContent content, long maxBytes, CancellationToken ct = default)
    {
        if (content.Headers.ContentLength > maxBytes) throw new ResponseTooLargeException(maxBytes);
        return new LimitedReadStream(await content.ReadAsStreamAsync(ct), maxBytes);
    }

    private sealed class LimitedReadStream(Stream inner, long maxBytes) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Count(int read)
        {
            _read += read;
            if (_read > maxBytes) throw new ResponseTooLargeException(maxBytes);
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
