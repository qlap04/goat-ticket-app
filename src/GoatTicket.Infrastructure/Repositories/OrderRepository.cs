using GoatTicket.Domain.Entities;
using GoatTicket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GoatTicket.Infrastructure.Repositories;

public class OrderRepository(GoatTicketDbContext db)
{
    public async Task<Order> CreateAsync(Order order, CancellationToken cancellationToken = default)
    {
        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        return order;
    }

    public Task<Order?> GetByIdAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        db.Orders.AsNoTracking().Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

    /// <summary>Loads a tracked order (with items) for the fulfillment processor to mutate — T033.</summary>
    public Task<Order?> GetTrackedByIdAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

    public Task<List<Order>> GetAllAsync(CancellationToken cancellationToken = default) =>
        db.Orders.AsNoTracking().Include(o => o.Items).OrderByDescending(o => o.CreatedAtUtc).ToListAsync(cancellationToken);

    /// <summary>
    /// FR-019: revenue by tier includes Fulfilled and FulfillmentFailed orders — payment
    /// succeeded for both. Only PaymentDeclined is excluded (data-model.md's revenue-by-tier
    /// query note).
    /// </summary>
    public async Task<List<TierRevenue>> GetRevenueByTierAsync(CancellationToken cancellationToken = default)
    {
        var items = await db.Orders
            .AsNoTracking()
            .Where(o => o.Status != OrderStatus.PaymentDeclined)
            .SelectMany(o => o.Items)
            .ToListAsync(cancellationToken);

        return items
            .GroupBy(i => i.Tier)
            .Select(g => new TierRevenue(g.Key, g.Sum(i => i.Price), g.Count()))
            .OrderBy(r => r.Tier)
            .ToList();
    }

    public async Task SetStatusAsync(Guid orderId, OrderStatus status, CancellationToken cancellationToken = default)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null)
        {
            return;
        }

        order.Status = status;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(Order order, CancellationToken cancellationToken = default)
    {
        db.Update(order);
        await db.SaveChangesAsync(cancellationToken);
    }
}
