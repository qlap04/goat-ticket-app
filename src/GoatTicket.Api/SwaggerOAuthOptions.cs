using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using Swashbuckle.AspNetCore.SwaggerUI;

namespace GoatTicket.Api;

/// <summary>
/// Wires Swagger UI's OAuth2 Authorization Code + PKCE flow against Entra ID
/// (research.md §8, contracts/openapi.yaml's `securitySchemes.entraId`) so login and every
/// protected endpoint can be exercised interactively without a separate frontend. This is a
/// public-client flow — no client secret is configured or needed.
/// </summary>
public static class SwaggerOAuthOptions
{
    public const string SchemeId = "entraId";

    public static void AddEntraIdSecurityScheme(this SwaggerGenOptions options, IConfiguration configuration)
    {
        var tenantId = configuration["SwaggerOAuth:TenantId"] ?? "{tenantId}";
        var scope = configuration["SwaggerOAuth:Scope"] ?? "access_as_user";

        options.AddSecurityDefinition(SchemeId, new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Flows = new OpenApiOAuthFlows
            {
                AuthorizationCode = new OpenApiOAuthFlow
                {
                    AuthorizationUrl = new Uri($"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/authorize"),
                    TokenUrl = new Uri($"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token"),
                    Scopes = new Dictionary<string, string>
                    {
                        [scope] = "Access the Goat Ticket API as the signed-in fan or admin"
                    }
                }
            }
        });

        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = SchemeId }
                },
                new[] { scope }
            }
        });
    }

    public static void ConfigureOAuth2(this SwaggerUIOptions options, IConfiguration configuration)
    {
        options.OAuthClientId(configuration["SwaggerOAuth:ClientId"]);
        options.OAuthUsePkce();
        options.OAuthScopeSeparator(" ");
    }
}
