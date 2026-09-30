using GoatTicket.Domain.Catalog;
using Microsoft.Azure.Cosmos;

namespace GoatTicket.Infrastructure.Catalog;

/// <summary>
/// Reads the event/ticket-tier catalog from Cosmos DB's `catalog` container (partition key
/// `/type`). Catalog data is configured ahead of on-sale and read-only at runtime (spec.md
/// Assumptions), so this client exposes reads only.
/// </summary>
public class CosmosCatalogClient(CosmosClient cosmosClient, string databaseId, string containerId)
{
    private Container Container => cosmosClient.GetContainer(databaseId, containerId);

    public async Task<EventDocument?> GetEventAsync(CancellationToken cancellationToken = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.type = @type")
            .WithParameter("@type", "event");

        using var iterator = Container.GetItemQueryIterator<EventDocument>(query);
        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(cancellationToken);
            return response.FirstOrDefault();
        }

        return null;
    }

    public async Task<IReadOnlyList<TicketTierDocument>> GetTicketTiersAsync(CancellationToken cancellationToken = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.type = @type")
            .WithParameter("@type", "ticketTier");

        var results = new List<TicketTierDocument>();
        using var iterator = Container.GetItemQueryIterator<TicketTierDocument>(query);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(cancellationToken);
            results.AddRange(response);
        }

        return results;
    }
}
