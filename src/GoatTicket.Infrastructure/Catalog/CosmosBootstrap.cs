using Microsoft.Azure.Cosmos;

namespace GoatTicket.Infrastructure.Catalog;

/// <summary>
/// Ensures the Cosmos `catalog` database/container exist. Freshly started Cosmos Emulator
/// containers (Docker Compose or Testcontainers) can accept a TCP connection before they are
/// actually ready to serve requests, so the first call here can transiently fail with a
/// <see cref="TaskCanceledException"/> or <see cref="CosmosException"/> even though the
/// container itself reports "started" — retry with backoff rather than a fixed startup delay,
/// since the actual warm-up time varies with host load (this is what flaked
/// GoatTicketApiFactory.InitializeAsync on CI).
/// </summary>
public static class CosmosBootstrap
{
    public static async Task<Container> EnsureCatalogContainerAsync(
        CosmosClient cosmosClient,
        string databaseId,
        string containerId,
        CancellationToken cancellationToken = default)
    {
        var delay = TimeSpan.FromSeconds(2);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var database = (await cosmosClient.CreateDatabaseIfNotExistsAsync(databaseId, cancellationToken: cancellationToken)).Database;
                return (await database.CreateContainerIfNotExistsAsync(containerId, "/type", cancellationToken: cancellationToken)).Container;
            }
            catch (Exception ex) when (attempt < 3 && (ex is TaskCanceledException or CosmosException))
            {
                await Task.Delay(delay, cancellationToken);
                delay *= 2;
            }
        }
    }
}
