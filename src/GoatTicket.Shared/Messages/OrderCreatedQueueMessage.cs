namespace GoatTicket.Shared.Messages;

/// <summary>
/// Message on `order-created`, produced by POST /api/orders after mocked payment succeeds,
/// consumed by OrderFulfillmentProcessor. Field names/types match
/// contracts/queue-messages.md exactly.
/// </summary>
public record OrderCreatedQueueMessage(
    Guid OrderId,
    DateTime CreatedAtUtc);
