using System.Security.Claims;
using GoatTicket.Infrastructure.Repositories;
using Microsoft.Identity.Web;

namespace GoatTicket.Api.Middleware;

/// <summary>Upserts the User row's profile fields (Email/DisplayName only) from Entra ID claims on every authenticated request.</summary>
public class UserProvisioningMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, UserRepository userRepository)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var userId = context.User.GetObjectId();
            if (!string.IsNullOrEmpty(userId))
            {
                var email = context.User.FindFirst("preferred_username")?.Value
                    ?? context.User.FindFirst(ClaimTypes.Upn)?.Value
                    ?? context.User.FindFirst(ClaimTypes.Email)?.Value
                    ?? $"{userId}@unknown.local";
                var displayName = context.User.FindFirst("name")?.Value ?? email;

                await userRepository.UpsertAsync(userId, email, displayName);
            }
        }

        await next(context);
    }
}
