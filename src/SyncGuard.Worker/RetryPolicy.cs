using System.Net.Http.Headers;

namespace SyncGuard.Worker;

public sealed class SyncOptions
{
    public Uri DestinationBaseUrl { get; set; } = new("http://localhost:5002/");
    public int BatchSize { get; set; } = 50;
    public int MaxParallelism { get; set; } = 8;
    public int MaxAttempts { get; set; } = 8;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    public bool IsValid() =>
        BatchSize > 0 && MaxParallelism > 0 && MaxAttempts > 0 && PollInterval > TimeSpan.Zero &&
        RequestTimeout > TimeSpan.Zero && BaseRetryDelay > TimeSpan.Zero && MaxRetryDelay >= BaseRetryDelay &&
        LeaseDuration > RequestTimeout * 2;
}

public static class RetryPolicy
{
    /// <summary>Capped exponential backoff with "equal jitter": uniformly in [d/2, d] where d = min(max, base * 2^(attempt-1)).</summary>
    public static TimeSpan Backoff(int attempt, TimeSpan baseDelay, TimeSpan maxDelay, double random01)
    {
        var exp = Math.Min(maxDelay.TotalMilliseconds, baseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1)));
        return TimeSpan.FromMilliseconds(exp / 2 + exp / 2 * random01);
    }

    /// <summary>Retry-After as a delay from now (delta-seconds or HTTP-date); null when absent.</summary>
    public static TimeSpan? RetryAfter(RetryConditionHeaderValue? header, DateTimeOffset now) => header switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } => date > now ? date - now : TimeSpan.Zero,
        _ => null,
    };

    /// <summary>408, 429 and 5xx are transient; every other non-success status is a permanent rejection of this payload.</summary>
    public static bool IsRetryable(int statusCode) => statusCode is 408 or 429 or >= 500;
}
