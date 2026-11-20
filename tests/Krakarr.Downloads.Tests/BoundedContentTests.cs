using System.Net.Http.Headers;
using Krakarr.Core.Http;

namespace Krakarr.Downloads.Tests;

public class BoundedContentTests
{
    /// <summary>Content without a Content-Length header (like a chunked response).</summary>
    private sealed class UnknownLengthContent(byte[] data) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(data).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reads_body_within_limit()
    {
        var data = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        Assert.Equal(data, await new ByteArrayContent(data).ReadAsByteArrayAsync(1000, Ct));
        Assert.Equal(data, await new UnknownLengthContent(data).ReadAsByteArrayAsync(1000, Ct));
    }

    [Fact]
    public async Task Rejects_declared_length_over_limit()
    {
        var content = new ByteArrayContent(new byte[10]);
        content.Headers.ContentLength = 10;
        await Assert.ThrowsAsync<ResponseTooLargeException>(() => content.ReadAsByteArrayAsync(9, Ct));
    }

    [Fact]
    public async Task Rejects_undeclared_body_over_limit()
    {
        await Assert.ThrowsAsync<ResponseTooLargeException>(() => new UnknownLengthContent(new byte[200_000]).ReadAsByteArrayAsync(100_000, Ct));
    }

    [Fact]
    public async Task Bounded_stream_throws_past_limit()
    {
        await using var stream = await new UnknownLengthContent(new byte[200_000]).ReadAsBoundedStreamAsync(100_000, Ct);
        await Assert.ThrowsAsync<ResponseTooLargeException>(() => stream.CopyToAsync(Stream.Null, Ct));
    }
}
