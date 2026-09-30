using GoatTicket.Domain.Entities;
using GoatTicket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GoatTicket.Infrastructure.Repositories;

public class UserRepository(GoatTicketDbContext db)
{
    public Task<User?> GetByIdAsync(string userId, CancellationToken cancellationToken = default) =>
        db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

    /// <summary>Upserts profile info from Entra ID claims on sign-in (Email/DisplayName only — never an admin flag, research.md §10).</summary>
    public async Task<User> UpsertAsync(string userId, string email, string displayName, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            user = new User { Id = userId, Email = email, DisplayName = displayName, CreatedAtUtc = DateTime.UtcNow };
            db.Users.Add(user);
        }
        else
        {
            user.Email = email;
            user.DisplayName = displayName;
        }

        await db.SaveChangesAsync(cancellationToken);
        return user;
    }
}
