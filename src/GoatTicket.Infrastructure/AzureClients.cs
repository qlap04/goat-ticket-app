using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace GoatTicket.Infrastructure;

/// <summary>
/// Factory methods for Azure SDK clients. Every deployed-environment client authenticates via
/// <see cref="DefaultAzureCredential"/> (constitution Principle III, research.md §2) — no
/// connection string, account key, or shared key. The only exception is the isolated
/// local-development path (Docker Compose emulators), which is confined to
/// `appsettings.Development.json`/`local.settings.json` and never used outside
/// <see cref="IHostEnvironment.IsDevelopment"/>.
/// </summary>
public static class AzureClients
{
    public static BlobServiceClient CreateBlobServiceClient(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            var connectionString = configuration["Storage:LocalDevConnectionString"] ?? "UseDevelopmentStorage=true";
            return new BlobServiceClient(connectionString);
        }

        var accountUri = configuration["Storage:AccountUri"]
            ?? throw new InvalidOperationException("Storage:AccountUri must be configured outside Development.");
        return new BlobServiceClient(new Uri(accountUri), new DefaultAzureCredential());
    }

    public static QueueServiceClient CreateQueueServiceClient(IConfiguration configuration, IHostEnvironment environment)
    {
        var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };

        if (environment.IsDevelopment())
        {
            var connectionString = configuration["Storage:LocalDevConnectionString"] ?? "UseDevelopmentStorage=true";
            return new QueueServiceClient(connectionString, options);
        }

        var accountUri = configuration["Storage:AccountUri"]
            ?? throw new InvalidOperationException("Storage:AccountUri must be configured outside Development.");
        return new QueueServiceClient(new Uri(accountUri), new DefaultAzureCredential(), options);
    }

    public static CosmosClient CreateCosmosClient(IConfiguration configuration, IHostEnvironment environment)
    {
        var endpoint = configuration["Cosmos:Endpoint"]
            ?? throw new InvalidOperationException("Cosmos:Endpoint must be configured.");

        if (environment.IsDevelopment())
        {
            var localDevKey = configuration["Cosmos:LocalDevKey"]
                ?? throw new InvalidOperationException("Cosmos:LocalDevKey must be configured in Development.");

            // The Cosmos DB Emulator uses a self-signed certificate; Gateway mode with a
            // relaxed HTTP handler is the standard local-dev workaround (never used outside
            // Development).
            return new CosmosClient(endpoint, localDevKey, new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                HttpClientFactory = () => new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                })
            });
        }

        return new CosmosClient(endpoint, new DefaultAzureCredential());
    }

    /// <summary>
    /// Builds the SQL Server connection string. In Development this is plain SQL
    /// authentication against the Docker Compose container; outside Development it uses
    /// Azure AD managed-identity authentication (`Authentication=Active Directory Default`),
    /// which is how `DefaultAzureCredential`-equivalent auth is expressed for
    /// Microsoft.Data.SqlClient/EF Core (there is no separate "SQL client object" to construct
    /// the way there is for Blob/Queue/Cosmos).
    /// </summary>
    public static string BuildSqlConnectionString(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            return configuration.GetConnectionString("GoatTicketDb")
                ?? throw new InvalidOperationException("ConnectionStrings:GoatTicketDb must be configured in Development.");
        }

        var accountUri = configuration["Sql:AccountUri"]
            ?? throw new InvalidOperationException("Sql:AccountUri must be configured outside Development.");
        return $"Server={accountUri};Database=GoatTicket;Authentication=Active Directory Default;Encrypt=True;";
    }
}
