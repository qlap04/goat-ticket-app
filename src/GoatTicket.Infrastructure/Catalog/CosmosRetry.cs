using System.Net;
using Microsoft.Azure.Cosmos;

namespace GoatTicket.Infrastructure.Catalog;

/// <summary>
/// Retries one Cosmos call with backoff when the failure is transient. A freshly started Cosmos
/// Emulator (Docker Compose or Testcontainers) answers the first requests slowly or with a 408
/// even though the container reports "started", and the host load decides how long that lasts, so
/// a fixed startup delay would be wrong. Only failures that can pass on their own are retried
/// (request timeout, throttling, service unavailable); a bad request is not, because it would fail
/// the same way every time.
/// </summary>
public static class CosmosRetry
{
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default,
        int maxAttempts = 4,
        TimeSpan? initialDelay = null)
    {
        var delay = initialDelay ?? TimeSpan.FromSeconds(2);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex, cancellationToken))
            {
                await Task.Delay(delay, cancellationToken);
                delay *= 2;
            }
        }
    }

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        // A timeout is transient, a cancellation the caller asked for is not.
        TaskCanceledException => !cancellationToken.IsCancellationRequested,
        CosmosException cosmos => cosmos.StatusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.ServiceUnavailable,
        _ => false
    };
}
