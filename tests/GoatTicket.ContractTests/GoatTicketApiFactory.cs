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
using Microsoft.Extensions.Configuration;
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
        // From configuration, so the tests create the database and container the deployed
        // application asks for. Cosmos ids are case sensitive, and these two names once disagreed
        // between the template and the application.
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var container = await CosmosBootstrap.EnsureCatalogContainerAsync(
            cosmosClient,
            configuration["Cosmos:DatabaseId"] ?? "GoatTicket",
            configuration["Cosmos:ContainerId"] ?? "catalog");

        await GoatTicket.Infrastructure.Seed.CatalogSeeder.SeedAsync(db, container);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await Task.WhenAll(_sql.DisposeAsync().AsTask(), _cosmos.DisposeAsync().AsTask(), _azurite.DisposeAsync().AsTask());
        await base.DisposeAsync();
    }

    /// <summary>
    /// The application settings the pipeline hands these tests, in the same shape the delivery
    /// pipeline writes to the App Service: a JSON array of { name, value } with '__' separating
    /// configuration sections. It arrives in APP_SETTINGS_JSON, which the pipeline maps from
    /// appSettingsJson in the goat-app-integration variable group.
    ///
    /// Reading the same contract here is the point: a setting name that drifts between these tests
    /// and the deployed application would otherwise leave the tests green and production broken.
    /// Running locally without the variable set is fine, the defaults apply.
    /// </summary>
    private static Dictionary<string, string?> PipelineAppSettings()
    {
        var json = Environment.GetEnvironmentVariable("APP_SETTINGS_JSON");
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string?>();
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().ToDictionary(
            entry => entry.GetProperty("name").GetString()!.Replace("__", ":"),
            entry => entry.TryGetProperty("value", out var value) ? value.GetString() : null);
    }

    private readonly Dictionary<string, string?> _appSettings = PipelineAppSettings();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Lowest precedence on purpose: the container endpoints applied below must win, because
        // Testcontainers assigns their ports at run time.
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(_appSettings));

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
