using Azure.Identity;
using GoatTicket.Api;
using GoatTicket.Api.Middleware;
using GoatTicket.Infrastructure;
using GoatTicket.Infrastructure.Catalog;
using GoatTicket.Infrastructure.Persistence;
using GoatTicket.Infrastructure.Repositories;
using GoatTicket.Infrastructure.Seed;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Key Vault (T013) — only outside Development; local dev reads secrets straight from
// appsettings.Development.json (research.md §2's local-dev exception).
if (!builder.Environment.IsDevelopment())
{
    var keyVaultUri = builder.Configuration["KeyVault:Uri"];
    if (!string.IsNullOrEmpty(keyVaultUri))
    {
        builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
    }
}

// Serilog (T021)
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// EF Core (T006, connection string built per research.md §2 in AzureClients)
builder.Services.AddDbContext<GoatTicketDbContext>(options =>
    options.UseSqlServer(AzureClients.BuildSqlConnectionString(builder.Configuration, builder.Environment)));

// Cosmos catalog + Storage queue clients (T009, T012)
builder.Services.AddSingleton(sp => AzureClients.CreateCosmosClient(builder.Configuration, builder.Environment));
builder.Services.AddSingleton(sp =>
{
    var cosmosClient = sp.GetRequiredService<CosmosClient>();
    var databaseId = builder.Configuration["Cosmos:DatabaseId"] ?? "GoatTicket";
    var containerId = builder.Configuration["Cosmos:ContainerId"] ?? "catalog";
    return new CosmosCatalogClient(cosmosClient, databaseId, containerId);
});
builder.Services.AddSingleton(sp => AzureClients.CreateQueueServiceClient(builder.Configuration, builder.Environment));

// Repositories (T010, T011)
builder.Services.AddScoped<SeatRepository>();
builder.Services.AddScoped<HoldRequestRepository>();
builder.Services.AddScoped<OrderRepository>();
builder.Services.AddScoped<UserRepository>();

// Entra ID authentication + AdminOnly policy from the "TicketAdmin" app-role claim (T019,
// research.md §10) — no stored IsAdmin flag is checked here; there is none.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireClaim("roles", "TicketAdmin"));
});

builder.Services.AddControllers();

// Swagger UI + OAuth2 Authorization Code + PKCE (T020, research.md §8)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.AddEntraIdSecurityScheme(builder.Configuration));

var app = builder.Build();

app.UseMiddleware<ErrorHandlingMiddleware>(); // T022

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Goat Ticket API v1");
    options.ConfigureOAuth2(builder.Configuration);
});

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseMiddleware<UserProvisioningMiddleware>();
app.UseAuthorization();
app.MapControllers();

// Local-dev-only seed entry point (T023): `dotnet run -- --seed`. Applies pending EF Core
// migrations, then seeds 20,000 seats + the Cosmos catalog. Never seeds an admin user
// (research.md §10) — grant admin access via Entra ID's TicketAdmin app role instead.
if (args.Contains("--seed"))
{
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;

    var db = services.GetRequiredService<GoatTicketDbContext>();
    await db.Database.MigrateAsync();

    var cosmosClient = services.GetRequiredService<CosmosClient>();
    var databaseId = builder.Configuration["Cosmos:DatabaseId"] ?? "GoatTicket";
    var containerId = builder.Configuration["Cosmos:ContainerId"] ?? "catalog";
    var container = await CosmosBootstrap.EnsureCatalogContainerAsync(cosmosClient, databaseId, containerId);

    await CatalogSeeder.SeedAsync(db, container);

    Log.Information("Seed complete: 20,000 seats + catalog documents.");
    return;
}

app.Run();

// Exposes the implicitly-generated Program class for WebApplicationFactory<Program> in tests.
public partial class Program;
