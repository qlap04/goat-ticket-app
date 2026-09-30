using GoatTicket.Domain.Entities;
using GoatTicket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GoatTicket.Infrastructure.Repositories;

/// <summary>
/// The only component that writes to <see cref="Seat"/> — Api/Functions call this instead of
/// EF Core directly (constitution Principle V). <see cref="TryGrantHoldAsync"/> is where the
/// exactly-one-hold-per-seat guarantee actually lives (research.md §1): it is gated on
/// <see cref="Seat.RowVersion"/>, not on queue ordering, so it stays correct even when the
/// caller (SeatHoldProcessor) is scaled out across many Function instances.
/// </summary>
public class SeatRepository(GoatTicketDbContext db)
{
    public Task<Seat?> GetByIdAsync(long seatId, CancellationToken cancellationToken = default) =>
        db.Seats.AsNoTracking().FirstOrDefaultAsync(s => s.Id == seatId, cancellationToken);

    public Task<List<Seat>> GetByTierAsync(Tier? tier, CancellationToken cancellationToken = default)
    {
        var query = db.Seats.AsNoTracking().AsQueryable();
        if (tier is not null)
        {
            query = query.Where(s => s.Tier == tier);
        }

        return query.OrderBy(s => s.Id).ToListAsync(cancellationToken);
    }

    public async Task<HoldGrantResult> TryGrantHoldAsync(
        long seatId,
        string userId,
        TimeSpan holdDuration,
        CancellationToken cancellationToken = default)
    {
        var seat = await db.Seats.FirstOrDefaultAsync(s => s.Id == seatId, cancellationToken);
        if (seat is null)
        {
            return HoldGrantResult.SeatNotFound;
        }

        if (seat.Status != SeatStatus.Available)
        {
            return HoldGrantResult.SeatNotAvailable;
        }

        seat.Status = SeatStatus.Held;
        seat.HeldByUserId = userId;
        seat.HoldExpiresAtUtc = DateTime.UtcNow.Add(holdDuration);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return HoldGrantResult.Granted;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another writer changed this seat's RowVersion between our read and our write —
            // treat it identically to "seat no longer available" (research.md §1). Do not
            // retry: a losing request should resolve fast (SC-001), not spin.
            return HoldGrantResult.SeatNotAvailable;
        }
    }

    /// <summary>Converts a held seat into a confirmed sale (FR-012), called after payment succeeds.</summary>
    public async Task MarkSoldAsync(long seatId, CancellationToken cancellationToken = default)
    {
        var seat = await db.Seats.FirstOrDefaultAsync(s => s.Id == seatId, cancellationToken);
        if (seat is null)
        {
            return;
        }

        seat.Status = SeatStatus.Sold;
        seat.HeldByUserId = null;
        seat.HoldExpiresAtUtc = null;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Extremely unlikely race with the expiry-release timer on the same seat; the
            // checkout that got here already validated an unexpired hold moments earlier.
        }
    }

    /// <summary>Immediately releases a held seat (FR-015: declined/abandoned checkout), rather than waiting for expiry.</summary>
    public async Task ReleaseHeldSeatAsync(long seatId, CancellationToken cancellationToken = default)
    {
        var seat = await db.Seats.FirstOrDefaultAsync(s => s.Id == seatId, cancellationToken);
        if (seat is null || seat.Status != SeatStatus.Held)
        {
            return;
        }

        seat.Status = SeatStatus.Available;
        seat.HeldByUserId = null;
        seat.HoldExpiresAtUtc = null;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Raced with the expiry-release timer already releasing the same seat — fine.
        }
    }

    /// <summary>
    /// Resets any seat whose hold has expired back to Available (FR-005, SC-005). Uses the
    /// same optimistic-concurrency pattern as <see cref="TryGrantHoldAsync"/> so a release
    /// racing a same-moment checkout cannot corrupt state (research.md §4) — whichever commits
    /// first wins, the other is silently skipped.
    /// </summary>
    public async Task<int> ReleaseExpiredAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var expiredSeatIds = await db.Seats
            .Where(s => s.Status == SeatStatus.Held && s.HoldExpiresAtUtc != null && s.HoldExpiresAtUtc < nowUtc)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        var releasedCount = 0;
        foreach (var seatId in expiredSeatIds)
        {
            var seat = await db.Seats.FirstOrDefaultAsync(s => s.Id == seatId, cancellationToken);
            if (seat is null || seat.Status != SeatStatus.Held || seat.HoldExpiresAtUtc is null || seat.HoldExpiresAtUtc >= nowUtc)
            {
                continue;
            }

            seat.Status = SeatStatus.Available;
            seat.HeldByUserId = null;
            seat.HoldExpiresAtUtc = null;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                releasedCount++;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Raced with a same-moment checkout completing (Held -> Sold) — the checkout
                // wins, there is nothing left to release for this seat.
            }
        }

        return releasedCount;
    }
}
