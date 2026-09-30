using GoatTicket.Domain.Catalog;
using GoatTicket.Domain.Entities;
using GoatTicket.Infrastructure.Persistence;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;

namespace GoatTicket.Infrastructure.Seed;

/// <summary>
/// Seeds catalog/seat DATA ONLY (20,000 seats, one Event document, five TicketTier
/// documents) — per quickstart.md step 2. This MUST NOT seed an admin user: admin access is
/// granted directly in Entra ID via the "TicketAdmin" app role, never via a database flag
/// (research.md §10).
/// </summary>
public static class CatalogSeeder
{
    private static readonly (Tier Tier, int Count, decimal Price, string DisplayName)[] TierPlan =
    [
        (Tier.VIP, 2000, 500.00m, "VIP"),
        (Tier.Standard, 10000, 150.00m, "Standard"),
        (Tier.StandA, 2667, 75.00m, "Stand A"),
        (Tier.StandB, 2667, 75.00m, "Stand B"),
        (Tier.StandC, 2666, 75.00m, "Stand C")
    ];

    public static async Task SeedAsync(
        GoatTicketDbContext db,
        Container catalogContainer,
        CancellationToken cancellationToken = default)
    {
        await SeedSeatsAsync(db, cancellationToken);
        await SeedCatalogAsync(catalogContainer, cancellationToken);
    }

    private static async Task SeedSeatsAsync(GoatTicketDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Seats.AnyAsync(cancellationToken))
        {
            return; // already seeded
        }

        long nextId = 1;
        foreach (var (tier, count, _, displayName) in TierPlan)
        {
            for (var i = 1; i <= count; i++)
            {
                db.Seats.Add(new Seat
                {
                    Id = nextId++,
                    Tier = tier,
                    SectionLabel = $"{displayName} - Seat {i}",
                    Status = SeatStatus.Available
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedCatalogAsync(Container catalogContainer, CancellationToken cancellationToken)
    {
        var eventDocument = new EventDocument
        {
            Id = "event-1000th-goal",
            Type = "event",
            Name = "The 1000th Goal Ceremony",
            Venue = "National Stadium",
            EventDateUtc = DateTime.UtcNow.AddMonths(2),
            OnSaleAtUtc = DateTime.UtcNow.AddMinutes(-5) // already open, for local quickstart convenience
        };

        await catalogContainer.UpsertItemAsync(
            eventDocument,
            new PartitionKey(eventDocument.Type),
            cancellationToken: cancellationToken);

        foreach (var (tier, count, price, displayName) in TierPlan)
        {
            var tierDocument = new TicketTierDocument
            {
                Id = $"tier-{tier.ToString().ToLowerInvariant()}",
                Type = "ticketTier",
                Tier = tier,
                DisplayName = displayName,
                Price = price,
                Capacity = count
            };

            await catalogContainer.UpsertItemAsync(
                tierDocument,
                new PartitionKey(tierDocument.Type),
                cancellationToken: cancellationToken);
        }
    }
}
