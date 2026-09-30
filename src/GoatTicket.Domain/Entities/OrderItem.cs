namespace GoatTicket.Domain.Entities;

/// <summary>
/// One seat within an order — supports multi-seat/group orders (FR-017).
/// <see cref="Tier"/> and <see cref="Price"/> are denormalized copies taken at sale time so
/// revenue-by-tier reporting (FR-019) stays stable even if catalog data changes later.
/// </summary>
public class OrderItem
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public long SeatId { get; set; }

    public Tier Tier { get; set; }

    public decimal Price { get; set; }

    /// <summary>Set once the PDF e-ticket is generated and uploaded (FR-013); null means not yet done.</summary>
    public string? TicketBlobUrl { get; set; }

    /// <summary>The unique token id (`jti`) embedded in the signed QR JWT (FR-013a), for traceability.</summary>
    public string? QrPayloadJti { get; set; }
}
