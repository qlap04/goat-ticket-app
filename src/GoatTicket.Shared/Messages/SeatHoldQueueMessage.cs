namespace GoatTicket.Shared.Messages;

/// <summary>
/// Message on `seat-hold-queue`, produced by POST /api/cart/hold, consumed by
/// SeatHoldProcessor. Field names/types match contracts/queue-messages.md exactly.
/// </summary>
public record SeatHoldQueueMessage(
    Guid HoldRequestId,
    long SeatId,
    string UserId,
    DateTime RequestedAtUtc);
