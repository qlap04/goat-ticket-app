using Azure.Identity;
using GoatTicket.Infrastructure;
using GoatTicket.Infrastructure.Catalog;
using GoatTicket.Infrastructure.Persistence;
using GoatTicket.Infrastructure.Repositories;
using GoatTicket.Shared.Ticketing;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Infrastructure;
using Serilog;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;

QuestPDF.Settings.License = LicenseType.Community;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration((context, configBuilder) =>
    {
        // App Configuration, then Key Vault — only outside Development; local dev reads
        // local.settings.json (research.md §2's local-dev exception).
        //
        // Unlabelled keys are shared by every environment; the labelled read that follows overrides
        // them for this one.
        if (!context.HostingEnvironment.IsDevelopment())
        {
            var builtConfig = configBuilder.Build();

            var appConfigurationEndpoint = builtConfig["AppConfiguration:Endpoint"];
            if (!string.IsNullOrEmpty(appConfigurationEndpoint))
            {
                var label = builtConfig["AppConfiguration:Label"];
                configBuilder.AddAzureAppConfiguration(options =>
                {
                    options.Connect(new Uri(appConfigurationEndpoint), new DefaultAzureCredential())
                        .Select(KeyFilter.Any, LabelFilter.Null);

                    if (!string.IsNullOrEmpty(label))
                    {
                        options.Select(KeyFilter.Any, label);
                    }
                });
                builtConfig = configBuilder.Build();
            }

            var keyVaultUri = builtConfig["KeyVault:Uri"];
            if (!string.IsNullOrEmpty(keyVaultUri))
            {
                configBuilder.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
            }
        }
    })
    .ConfigureServices((context, services) =>
    {
        var configuration = context.Configuration;
        var environment = context.HostingEnvironment;

        // EF Core (T006, connection string per research.md §2 in AzureClients)
        services.AddDbContext<GoatTicketDbContext>(options =>
            options.UseSqlServer(AzureClients.BuildSqlConnectionString(configuration, environment)));

        // Cosmos catalog, Blob, and Queue clients (T009, T012)
        services.AddSingleton(sp => AzureClients.CreateCosmosClient(configuration, environment));
        services.AddSingleton(sp =>
        {
            var cosmosClient = sp.GetRequiredService<CosmosClient>();
            var databaseId = configuration["Cosmos:DatabaseId"] ?? "GoatTicket";
            var containerId = configuration["Cosmos:ContainerId"] ?? "catalog";
            return new CosmosCatalogClient(cosmosClient, databaseId, containerId);
        });
        services.AddSingleton(sp => AzureClients.CreateBlobServiceClient(configuration, environment));
        services.AddSingleton(sp => AzureClients.CreateQueueServiceClient(configuration, environment));

        // Repositories (T010, T011)
        services.AddScoped<SeatRepository>();
        services.AddScoped<HoldRequestRepository>();
        services.AddScoped<OrderRepository>();
        services.AddScoped<UserRepository>();

        // Dedicated RS256 TicketQrSigningKey (T015, research.md §6/§10) — distinct from the
        // auth JwtSigningKey, which this project never touches.
        services.AddSingleton(sp =>
        {
            var pem = environment.IsDevelopment()
                ? configuration["Signing:TicketQrPrivateKeyPemLocalDev"]
                : configuration["Signing:TicketQrSigningKeyPem"];
            if (string.IsNullOrEmpty(pem))
            {
                throw new InvalidOperationException("Ticket QR signing key (TicketQrSigningKey) is not configured.");
            }

            return TicketQrSigner.FromPrivateKeyPem(pem);
        });
    })
    .UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console())
    .Build();

host.Run();
