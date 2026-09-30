using GoatTicket.Domain.Entities;

namespace GoatTicket.Infrastructure.Repositories;

public record TierRevenue(Tier Tier, decimal TotalRevenue, int TicketsSold);
