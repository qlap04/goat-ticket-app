namespace GoatTicket.Domain.Entities;

public enum OrderStatus
{
    PaymentSucceeded,
    PaymentDeclined,
    Fulfilled,

    /// <summary>
    /// Payment succeeded but ticket generation/email delivery exhausted all retries
    /// (research.md §9). Counts as revenue (FR-019) — it is not a payment failure.
    /// </summary>
    FulfillmentFailed
}
