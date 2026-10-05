# Goat Ticket — Commemorative Event Ticket Booking

Ticket booking platform for a one-off "1000th Goal" ceremony: fans sign in with Microsoft Entra
ID, browse a 20,000-seat map, place a temporary hold, check out through a mocked payment step,
and receive a PDF e-ticket with a QR code by email. See
[specs/001-seat-hold-ticketing/](specs/001-seat-hold-ticketing/) for the full spec, plan,
research, data model, and API contracts.

## Solution layout

- `src/GoatTicket.Api` — ASP.NET Core 8 Web API (controllers, Entra ID auth, Swagger UI)
- `src/GoatTicket.Functions` — Azure Functions isolated worker (seat-hold processing, order
  fulfillment, hold-expiry timer, fulfillment-failure poison handler)
- `src/GoatTicket.Domain` — entities, enums (no I/O)
- `src/GoatTicket.Infrastructure` — EF Core, Cosmos, Blob/Queue clients, repositories, seed script
- `src/GoatTicket.Shared` — DTOs shared between Api and Functions (queue messages, QR signer)
- `tests/GoatTicket.UnitTests`, `tests/GoatTicket.IntegrationTests`, `tests/GoatTicket.ContractTests`

## Prerequisites

- .NET 8 SDK (pinned via `global.json`; on macOS via Homebrew: `brew install dotnet@8`, then
  `export DOTNET_ROOT="$(brew --prefix dotnet@8)/libexec"` and prepend
  `$(brew --prefix dotnet@8)/bin` to `PATH` before running any `dotnet` command in this repo,
  since `dotnet@8` is keg-only and won't be first on `PATH` by default)
- Docker Desktop (for `docker-compose.yml`: SQL Server, Cosmos DB Emulator, Azurite, MailHog)
- `dotnet tool install --global dotnet-ef` (for migrations)

> **Apple Silicon note**: the default SQL Server and Cosmos DB Emulator images in
> `docker-compose.yml` are amd64-only and run under emulation, which can be slow or, on
> memory-constrained Docker Desktop configurations, crash on startup. If that happens locally,
> swap in `mcr.microsoft.com/azure-sql-edge:latest` (arm64-native, wire-compatible with SQL
> Server) for local dev only — see the comments in `docker-compose.yml`.

## Local setup

```bash
docker compose up -d

dotnet ef database update \
  --project src/GoatTicket.Infrastructure \
  --startup-project src/GoatTicket.Api

dotnet run --project src/GoatTicket.Api -- --seed   # 20,000 seats + Cosmos catalog; never seeds an admin
```

Run the API and Functions host in separate terminals:

```bash
dotnet run --project src/GoatTicket.Api
func start --script-root src/GoatTicket.Functions   # requires Azure Functions Core Tools v4
```

Open Swagger UI (`https://localhost:<port>/swagger`), click **Authorize**, and sign in with a
Microsoft Entra ID account (Authorization Code + PKCE — no client secret) to exercise the API
interactively. See [quickstart.md](specs/001-seat-hold-ticketing/quickstart.md) for the full
walkthrough of all three user stories.

### Granting admin access

Admin status is **not** a database flag — it's the Entra ID `TicketAdmin` app-role claim,
checked on every request (see `research.md` §10). To test the admin endpoints
(`GET /api/admin/orders`, `GET /api/admin/revenue-by-tier`) locally:

1. In your Entra ID app registration: **App roles** → create a role named `TicketAdmin`.
2. In **Enterprise applications** → your app → **Users and groups**: assign your test account
   that role.
3. Sign in via Swagger UI with that account — its token will carry the `TicketAdmin` role claim.

## Running tests

```bash
dotnet test                                            # full suite
dotnet test --filter FullyQualifiedName~TicketQrSigner # fast, no Docker required
```

Most test classes spin up their own SQL Server / Cosmos DB Emulator / Azurite containers via
Testcontainers — no manual `docker compose up` is required to run `dotnet test`, but Docker
itself must be running. On resource-constrained or ARM Docker hosts, the SQL Server/Cosmos
Emulator containers can be slow to pull and start (occasionally 10+ minutes on first pull); a
CI runner with native amd64 Docker will be substantially faster.

## Configuration

Non-secret settings (tenant/client IDs, resource endpoints, queue/container names) live in
`appsettings.json` / `appsettings.Development.json` / `local.settings.json`. Actual secrets
(`JwtSigningKey`, `TicketQrSigningKey`) are never stored in this repo — outside `Development`
they are fetched from Key Vault via `DefaultAzureCredential` (see `research.md` §2, §6, §10 and
the project constitution's Principle III). 
