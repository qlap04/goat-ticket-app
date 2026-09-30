using GoatTicket.Domain.Entities;

namespace GoatTicket.Api.Contracts;

public record HoldRequestInput(long SeatId);

public record CheckoutInput(List<Guid> HoldRequestIds, MockPaymentInput Payment);

/// <summary>Mocked payment gateway input (FR-010, FR-011) — no real payment processor involved.</summary>
public record MockPaymentInput(string Simulate); // "succeed" | "decline"

public static class OrderMappingExtensions
{
    public static object ToDto(this Order order) => new
    {
        id = order.Id,
        userId = order.UserId,
        status = order.Status.ToString(),
        totalAmount = order.TotalAmount,
        items = order.Items.Select(i => new
        {
            seatId = i.SeatId,
            tier = i.Tier.ToString(),
            price = i.Price,
            ticketBlobUrl = i.TicketBlobUrl
        }),
        createdAtUtc = order.CreatedAtUtc
    };
}
