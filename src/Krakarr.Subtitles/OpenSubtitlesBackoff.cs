using System.Globalization;
using System.Net;
using System.Text.Json;
using Krakarr.Core.Http;
using Polly;

namespace Krakarr.Subtitles;

/// <summary>
/// Remembers when OpenSubtitles told us to back off, shared by every request of the process: after a 429 nothing is
/// sent until the rate limit resets, and after the daily download quota is used up (406 on <c>POST /download</c>) no
/// download is requested until the quota renews. Searching keeps working while only the download quota is exhausted.
/// </summary>
public sealed class OpenSubtitlesBackoff(TimeProvider time)
{
    /// <summary>Wait used after a 429 without Retry-After / ratelimit-reset headers.</summary>
    internal static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromSeconds(10);
    /// <summary>Wait used after a quota 406 whose body does not say when the quota renews.</summary>
    internal static readonly TimeSpan DefaultQuotaWait = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(24);

    private long _requestsUntilTicks;
    private long _downloadsUntilTicks;

    /// <summary>No request at all before this time (rate limited), or null.</summary>
    public DateTimeOffset? RequestsBlockedUntil => Active(ref _requestsUntilTicks);

    /// <summary>No download before this time (daily quota used up, or rate limited), or null.</summary>
    public DateTimeOffset? DownloadsBlockedUntil => Max(Active(ref _downloadsUntilTicks), RequestsBlockedUntil);

    internal void BlockRequests(TimeSpan wait) => Extend(ref _requestsUntilTicks, wait);

    internal void BlockDownloads(TimeSpan wait) => Extend(ref _downloadsUntilTicks, wait);

    private DateTimeOffset? Active(ref long ticks)
    {
        var value = Interlocked.Read(ref ticks);
        return value > time.GetUtcNow().UtcTicks ? new DateTimeOffset(value, TimeSpan.Zero) : null;
    }

    private void Extend(ref long ticks, TimeSpan wait)
    {
        wait = wait <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : wait > MaxWait ? MaxWait : wait;
        var until = (time.GetUtcNow() + wait).UtcTicks;
        long current;
        do
        {
            current = Interlocked.Read(ref ticks);
            if (current >= until) return;
        } while (Interlocked.CompareExchange(ref ticks, until, current) != current);
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    internal static bool IsDownloadRequest(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/download", StringComparison.Ordinal) == true;

    /// <summary>How long a 429 asks us to wait: Retry-After, then ratelimit-reset (seconds), then a default.</summary>
    internal static TimeSpan RateLimitWait(HttpResponseMessage response, DateTimeOffset now)
    {
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta) return delta;
            if (retryAfter.Date is { } date) return date - now;
        }

        if (response.Headers.TryGetValues("ratelimit-reset", out var values)
            && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(seconds);

        return DefaultRateLimitWait;
    }

    /// <summary>How long a quota 406 asks us to wait, from its <c>reset_time_utc</c>, or a default.</summary>
    internal static async Task<TimeSpan> QuotaWaitAsync(HttpResponseMessage response, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            // Read the (small) error body, then put it back so the provider can still report the API's message.
            var original = response.Content;
            var body = await original.ReadAsByteArrayAsync(64 * 1024, ct);
            response.Content = new ByteArrayContent(body);
            foreach (var header in original.Headers) response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            original.Dispose();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("reset_time_utc", out var reset)
                && reset.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(reset.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                return at - now;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException)
        {
        }

        return DefaultQuotaWait;
    }
}

/// <summary>
/// Polly strategy in the OpenSubtitles <see cref="HttpClient"/> pipeline that fails fast with
/// <see cref="SubtitleRateLimitException"/> while <see cref="OpenSubtitlesBackoff"/> says to wait, and starts the wait
/// when a response reports a rate limit or an exhausted download quota.
/// </summary>
/// <remarks>
/// Polly's built-in circuit breaker needs at least two calls in its sampling window before it can open, so it would let
/// another request through after the first 429; this strategy stops at the first one and honours Retry-After.
/// </remarks>
internal sealed class OpenSubtitlesBackoffStrategy(OpenSubtitlesBackoff backoff, TimeProvider time) : ResilienceStrategy<HttpResponseMessage>
{
    protected override async ValueTask<Outcome<HttpResponseMessage>> ExecuteCore<TState>(
        Func<ResilienceContext, TState, ValueTask<Outcome<HttpResponseMessage>>> callback, ResilienceContext context, TState state)
    {
        var request = context.GetRequestMessage();
        var isDownload = request is not null && OpenSubtitlesBackoff.IsDownloadRequest(request);

        if (backoff.RequestsBlockedUntil is { } requestsUntil)
            return Outcome.FromException<HttpResponseMessage>(SubtitleRateLimitException.RateLimited(requestsUntil));
        if (isDownload && backoff.DownloadsBlockedUntil is { } downloadsUntil)
            return Outcome.FromException<HttpResponseMessage>(SubtitleRateLimitException.QuotaReached(downloadsUntil));

        var outcome = await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
        if (outcome.Result is not { } response) return outcome;

        var now = time.GetUtcNow();
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            backoff.BlockRequests(OpenSubtitlesBackoff.RateLimitWait(response, now));
        else if (response.StatusCode == HttpStatusCode.NotAcceptable && isDownload)
            backoff.BlockDownloads(await OpenSubtitlesBackoff.QuotaWaitAsync(response, now, context.CancellationToken).ConfigureAwait(false));

        return outcome;
    }
}

internal sealed class OpenSubtitlesBackoffStrategyOptions : ResilienceStrategyOptions
{
    public OpenSubtitlesBackoffStrategyOptions() => Name = "OpenSubtitlesBackoff";
}

public static class OpenSubtitlesBackoffExtensions
{
    /// <summary>Adds the <see cref="OpenSubtitlesBackoff"/> gate to an HTTP resilience pipeline.</summary>
    public static ResiliencePipelineBuilder<HttpResponseMessage> AddOpenSubtitlesBackoff(
        this ResiliencePipelineBuilder<HttpResponseMessage> builder, OpenSubtitlesBackoff backoff, TimeProvider time) =>
        builder.AddStrategy(_ => new OpenSubtitlesBackoffStrategy(backoff, time), new OpenSubtitlesBackoffStrategyOptions());
}
