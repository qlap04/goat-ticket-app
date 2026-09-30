using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using GoatTicket.Infrastructure.Catalog;
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
    // Pinned to 2022: the default 2019-CU18 image crashes on startup (SIGABRT) under QEMU
    // emulation on Apple Silicon Docker hosts.
    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();
    private readonly CosmosDbContainer _cosmos = new CosmosDbBuilder().Build();
    // Pinned to latest: Testcontainers.Azurite 3.10.0's default image predates the storage API
    // version (2024-08-04) that Azure.Storage.Blobs 12.21.2 requests.
    private readonly AzuriteContainer _azurite = new AzuriteBuilder()
        .WithImage("mcr.microsoft.com/azure-storage/azurite:latest")
        .Build();

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
        var container = await CosmosBootstrap.EnsureCatalogContainerAsync(cosmosClient, "GoatTicket", "catalog");

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
                }),
                // See AzureClients.CreateCosmosClient: the catalog POCOs use
                // System.Text.Json's [JsonPropertyName], which the SDK's default serializer
                // ignores without this.
                UseSystemTextJsonSerializerWithOptions = JsonSerializerOptions.Default,
                // The emulator running under Testcontainers on a loaded CI box can take well
                // past the SDK's default request timeout to respond — bump it so slow-but-alive
                // beats a flaky 408.
                RequestTimeout = TimeSpan.FromSeconds(60)
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
