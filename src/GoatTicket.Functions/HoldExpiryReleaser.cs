using GoatTicket.Infrastructure.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GoatTicket.Functions;

/// <summary>
/// Timer trigger, every minute: releases any seat whose hold has expired back to Available
/// (FR-005, SC-005). Uses the same optimistic-concurrency pattern as SeatHoldProcessor so a
/// release racing a same-moment checkout cannot corrupt state (research.md §4).
/// </summary>
public class HoldExpiryReleaser(SeatRepository seatRepository, ILogger<HoldExpiryReleaser> logger)
{
    [Function(nameof(HoldExpiryReleaser))]
    public async Task RunAsync([TimerTrigger("0 */1 * * * *")] TimerInfo timerInfo)
    {
        var releasedCount = await seatRepository.ReleaseExpiredAsync(DateTime.UtcNow);
        if (releasedCount > 0)
        {
            logger.LogInformation("Released {ReleasedCount} expired seat hold(s).", releasedCount);
        }
    }
}
