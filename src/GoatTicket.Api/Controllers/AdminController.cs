using GoatTicket.Api.Contracts;
using GoatTicket.Infrastructure.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GoatTicket.Api.Controllers;

/// <summary>
/// FR-018 through FR-020: admin-only order list and revenue-by-tier reporting. Authorization
/// is the "AdminOnly" policy (Program.cs), which checks the Entra ID "TicketAdmin" app-role
/// claim directly — there is no stored admin flag (research.md §10).
/// </summary>
[ApiController]
[Authorize(Policy = "AdminOnly")]
[Route("api/admin")]
public class AdminController(OrderRepository orderRepository) : ControllerBase
{
    [HttpGet("orders")]
    public async Task<IActionResult> GetOrders(CancellationToken cancellationToken)
    {
        var orders = await orderRepository.GetAllAsync(cancellationToken);
        return Ok(orders.Select(o => o.ToDto()));
    }

    [HttpGet("revenue-by-tier")]
    public async Task<IActionResult> GetRevenueByTier(CancellationToken cancellationToken)
    {
        // FR-019: includes Fulfilled and FulfillmentFailed orders, excludes only
        // PaymentDeclined (data-model.md's revenue-by-tier query note).
        var revenue = await orderRepository.GetRevenueByTierAsync(cancellationToken);
        return Ok(revenue.Select(r => new
        {
            tier = r.Tier.ToString(),
            totalRevenue = r.TotalRevenue,
            ticketsSold = r.TicketsSold
        }));
    }
}
