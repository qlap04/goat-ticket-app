using GoatTicket.Domain.Entities;
using GoatTicket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GoatTicket.Infrastructure.Repositories;

public class HoldRequestRepository(GoatTicketDbContext db)
{
    public async Task<HoldRequest> CreateAsync(long seatId, string userId, CancellationToken cancellationToken = default)
    {
        var holdRequest = new HoldRequest
        {
            Id = Guid.NewGuid(),
            SeatId = seatId,
            UserId = userId,
            Status = HoldRequestStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow
        };

        db.HoldRequests.Add(holdRequest);
        await db.SaveChangesAsync(cancellationToken);
        return holdRequest;
    }

    public Task<HoldRequest?> GetByIdAsync(Guid holdRequestId, CancellationToken cancellationToken = default) =>
        db.HoldRequests.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdRequestId, cancellationToken);

    public async Task SetStatusAsync(
        Guid holdRequestId,
        HoldRequestStatus status,
        string? failureReason,
        CancellationToken cancellationToken = default)
    {
        var holdRequest = await db.HoldRequests.FirstOrDefaultAsync(h => h.Id == holdRequestId, cancellationToken);
        if (holdRequest is null)
        {
            return;
        }

        holdRequest.Status = status;
        holdRequest.FailureReason = failureReason;
        await db.SaveChangesAsync(cancellationToken);
    }
}
