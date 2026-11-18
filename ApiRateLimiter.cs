using System.Net.Http.Headers;

namespace SpotifyToTidal;

/// <summary>
/// Coordinates request admission and 429 cooldowns across every worker using one API.
/// A numeric budget is opt-in: vendors that do not publish one must not be assigned
/// a fabricated "official" limit.
/// </summary>
public sealed class ApiRateLimiter
{
    private readonly object gate = new();
    private readonly Queue<DateTimeOffset> recentCalls = new();
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> pause;
    private readonly int? maxRequests;
    private readonly TimeSpan window;
    private readonly string provider;
    private DateTimeOffset blockedUntil = DateTimeOffset.MinValue;
    private int consecutive429;

    public ApiRateLimiter(string provider, int? maxRequests, TimeSpan window,
        Func<TimeSpan, CancellationToken, Task>? pause = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        if (maxRequests is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRequests),
                "A local request budget must be greater than zero.");
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));
        this.provider = provider;
        this.maxRequests = maxRequests;
        this.window = window;
        this.pause = pause ?? Task.Delay;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Atomically reserves a sliding-window slot when configured, while also
    /// observing a shared cooldown created by any parallel request's 429.
    /// </summary>
    public async Task WaitForTurnAsync(CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            bool localBudget;
            lock (gate)
            {
                var now = utcNow();
                while (recentCalls.Count != 0 && recentCalls.Peek() + window <= now)
                    recentCalls.Dequeue();

                if (blockedUntil > now)
                {
                    wait = blockedUntil - now;
                    localBudget = false;
                }
                else if (maxRequests is { } budget && recentCalls.Count >= budget)
                {
                    wait = recentCalls.Peek() + window - now;
                    localBudget = true;
                }
                else
                {
                    if (maxRequests.HasValue)
                        recentCalls.Enqueue(now);
                    return;
                }
            }

            if (localBudget)
                Console.Error.WriteLine(
                    $"Info: {provider} local rate limit ({maxRequests} calls/{window.TotalSeconds:0.#}s); " +
                    $"waiting {wait.TotalSeconds:0.###}s before sending another request.");
            await pause(wait, ct);
        }
    }

    /// <summary>
    /// Register a provider-wide HTTP 429. A valid Retry-After takes precedence,
    /// even when it exceeds 60 seconds. Without guidance, use a bounded
    /// exponential 2, 4, 8, ... 60-second fallback until an API call succeeds.
    /// </summary>
    public (TimeSpan Delay, string Policy) Register429(RetryConditionHeaderValue? retryAfter)
    {
        lock (gate)
        {
            var now = utcNow();
            TimeSpan delay;
            string policy;
            if (retryAfter?.Delta is { } seconds && seconds >= TimeSpan.Zero)
            {
                delay = seconds;
                policy = $"server Retry-After={seconds.TotalSeconds:0.###}s (delta-seconds)";
            }
            else if (retryAfter?.Date is { } date)
            {
                delay = date > now ? date - now : TimeSpan.Zero;
                policy = $"server Retry-After HTTP-date={date:O}";
            }
            else
            {
                delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(consecutive429 + 1, 6))));
                policy = "exponential backoff (2, 4, 8, 16, 32, 60s; reset on success)";
            }

            if (consecutive429 < 6)
                consecutive429++;
            var until = now + delay;
            if (until > blockedUntil)
                blockedUntil = until;
            // An earlier, longer Retry-After still governs concurrent workers.
            return (blockedUntil > now ? blockedUntil - now : TimeSpan.Zero, policy);
        }
    }

    public void RecordSuccess()
    {
        lock (gate)
            consecutive429 = 0;
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => pause(delay, ct);

    public static (TimeSpan Delay, string Policy) ServerRetry(
        RetryConditionHeaderValue? retryAfter, int attempt, DateTimeOffset now)
    {
        if (retryAfter?.Delta is { } seconds && seconds >= TimeSpan.Zero)
            return (seconds, $"server Retry-After={seconds.TotalSeconds:0.###}s (delta-seconds)");
        if (retryAfter?.Date is { } date)
            return (date > now ? date - now : TimeSpan.Zero,
                $"server Retry-After HTTP-date={date:O}");
        return (TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt + 1))),
            "exponential backoff (2, 4, 8, 16s for four retries)");
    }
}
