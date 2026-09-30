using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using GoatTicket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.Azurite;
using Testcontainers.CosmosDb;
using Testcontainers.MsSql;
using Xunit;

namespace GoatTicket.ContractTests;

/// <summary>
/// Boots the real Api pipeline (auth, controllers, repositories) against Testcontainers-backed
/// SQL Server, Cosmos DB Emulator, and Azurite — the same infrastructure shape as production,
/// per research.md §7 (real emulators over mocks). Authentication is swapped for
/// <see cref="TestAuthHandler"/> so tests can exercise [Authorize]/"AdminOnly" without a real
/// Entra ID token.
/// </summary>
public class GoatTicketApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder().Build();
    private readonly CosmosDbContainer _cosmos = new CosmosDbBuilder().Build();
    private readonly AzuriteContainer _azurite = new AzuriteBuilder().Build();

    public string SqlConnectionString => _sql.GetConnectionString();
    public string CosmosConnectionString => _cosmos.GetConnectionString();
    public string AzuriteConnectionString => _azurite.GetConnectionString();

    public HttpClient CreateAuthenticatedClient(string userId, bool isAdmin = false)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", userId);
        if (isAdmin)
        {
            client.DefaultRequestHeaders.Add("X-Test-IsAdmin", "true");
        }

        return client;
    }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_sql.StartAsync(), _cosmos.StartAsync(), _azurite.StartAsync());

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GoatTicketDbContext>();
        await db.Database.MigrateAsync();

        var cosmosClient = scope.ServiceProvider.GetRequiredService<CosmosClient>();
        var database = (await cosmosClient.CreateDatabaseIfNotExistsAsync("GoatTicket")).Database;
        var container = (await database.CreateContainerIfNotExistsAsync("catalog", "/type")).Container;

        await GoatTicket.Infrastructure.Seed.CatalogSeeder.SeedAsync(db, container);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await Task.WhenAll(_sql.DisposeAsync().AsTask(), _cosmos.DisposeAsync().AsTask(), _azurite.DisposeAsync().AsTask());
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            services.PostConfigure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(o =>
            {
                o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            });

            services.RemoveAll<DbContextOptions<GoatTicketDbContext>>();
            services.AddDbContext<GoatTicketDbContext>(options => options.UseSqlServer(_sql.GetConnectionString()));

            services.RemoveAll<CosmosClient>();
            services.AddSingleton(_ => new CosmosClient(_cosmos.GetConnectionString(), new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                HttpClientFactory = () => new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                })
            }));

            services.RemoveAll<BlobServiceClient>();
            services.AddSingleton(_ => new BlobServiceClient(_azurite.GetConnectionString()));

            services.RemoveAll<QueueServiceClient>();
            services.AddSingleton(_ => new QueueServiceClient(_azurite.GetConnectionString(), new QueueClientOptions
            {
                MessageEncoding = QueueMessageEncoding.Base64
            }));
        });
    }
}
