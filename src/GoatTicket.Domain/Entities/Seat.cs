namespace GoatTicket.Domain.Entities;

/// <summary>
/// One of the 20,000 individually identified seats in the venue (data-model.md).
/// <see cref="RowVersion"/> is the optimistic-concurrency token that is the actual mechanism
/// enforcing exactly-one-hold-per-seat (research.md §1) — not the queue's batchSize alone.
/// </summary>
public class Seat
{
    public long Id { get; set; }

    public Tier Tier { get; set; }

    public required string SectionLabel { get; set; }

    public SeatStatus Status { get; set; } = SeatStatus.Available;

    public string? HeldByUserId { get; set; }

    public DateTime? HoldExpiresAtUtc { get; set; }

    public byte[]? RowVersion { get; set; }
}
