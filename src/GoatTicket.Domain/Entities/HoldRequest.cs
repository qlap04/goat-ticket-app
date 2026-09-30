namespace GoatTicket.Domain.Entities;

/// <summary>
/// One row per hold *attempt* — this is what the client polls against (FR-007, FR-008).
/// </summary>
public class HoldRequest
{
    public Guid Id { get; set; }

    public long SeatId { get; set; }

    public required string UserId { get; set; }

    public HoldRequestStatus Status { get; set; } = HoldRequestStatus.Pending;

    public string? FailureReason { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
