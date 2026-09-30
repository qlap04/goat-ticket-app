using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GoatTicket.ContractTests;

/// <summary>
/// Test-only auth scheme: a request carrying `X-Test-UserId` is authenticated as that user;
/// `X-Test-IsAdmin: true` additionally grants the `TicketAdmin` role claim the "AdminOnly"
/// policy checks (research.md §10). Lets contract tests exercise [Authorize] endpoints without
/// a real Entra ID token.
/// </summary>
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-UserId", out var userId) || string.IsNullOrEmpty(userId))
        {
            return Task.FromResult(AuthenticateResult.Fail("Missing X-Test-UserId header."));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId!),
            new("http://schemas.microsoft.com/identity/claims/objectidentifier", userId!),
            new(ClaimTypes.Email, $"{userId}@test.local"),
            new("name", userId!)
        };

        if (Request.Headers.TryGetValue("X-Test-IsAdmin", out var isAdmin) && isAdmin == "true")
        {
            claims.Add(new Claim("roles", "TicketAdmin"));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
