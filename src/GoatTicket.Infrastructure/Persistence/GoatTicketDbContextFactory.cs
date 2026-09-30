using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GoatTicket.Infrastructure.Persistence;

/// <summary>
/// Design-time factory so `dotnet ef migrations add` works from this project directly,
/// independent of the Api/Functions startup projects' DI wiring.
/// </summary>
public class GoatTicketDbContextFactory : IDesignTimeDbContextFactory<GoatTicketDbContext>
{
    public GoatTicketDbContext CreateDbContext(string[] args)
    {
        // T046 audit note: this connection string is only ever used by the `dotnet-ef` design-time
        // CLI (schema/migration generation) against the local Docker Compose SQL container — it is
        // never referenced by application startup (AzureClients.BuildSqlConnectionString is what
        // Program.cs uses at runtime) and never runs outside a developer's machine. Matches the
        // same local-only SA password as docker-compose.yml, per the constitution's Principle III
        // local-development exception (research.md §2).
        var optionsBuilder = new DbContextOptionsBuilder<GoatTicketDbContext>();
        optionsBuilder.UseSqlServer(
            "Server=localhost,1433;Database=GoatTicket;User Id=sa;Password=LocalDev!Passw0rd;TrustServerCertificate=True");
        return new GoatTicketDbContext(optionsBuilder.Options);
    }
}
