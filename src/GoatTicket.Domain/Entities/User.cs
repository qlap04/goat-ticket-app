namespace GoatTicket.Domain.Entities;

/// <summary>
/// A Fan or Admin. Admin status is never stored here — it is derived per-request from the
/// Entra ID "TicketAdmin" app-role claim (research.md §10), so this table has no admin flag.
/// </summary>
public class User
{
    /// <summary>Entra ID <c>oid</c> claim — the durable, unique identifier from the identity provider.</summary>
    public required string Id { get; set; }

    public required string Email { get; set; }

    public required string DisplayName { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
