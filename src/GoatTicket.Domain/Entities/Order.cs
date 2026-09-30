namespace GoatTicket.Domain.Entities;

public class Order
{
    public Guid Id { get; set; }

    public required string UserId { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.PaymentSucceeded;

    public decimal TotalAmount { get; set; }

    /// <summary>
    /// Set immediately after the confirmation email is successfully sent; checked before
    /// sending on every retry so a redelivered fulfillment message never sends a duplicate
    /// email (research.md §9).
    /// </summary>
    public DateTime? EmailSentAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public List<OrderItem> Items { get; set; } = [];
}
