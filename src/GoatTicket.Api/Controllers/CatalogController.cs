using GoatTicket.Domain.Entities;
using GoatTicket.Infrastructure.Catalog;
using GoatTicket.Infrastructure.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GoatTicket.Api.Controllers;

/// <summary>FR-002, FR-002a: browse the seating map and ticket tiers.</summary>
[ApiController]
[Authorize]
[Route("api/catalog")]
public class CatalogController(CosmosCatalogClient catalogClient, SeatRepository seatRepository) : ControllerBase
{
    [HttpGet("event")]
    public async Task<IActionResult> GetEvent(CancellationToken cancellationToken)
    {
        var eventDocument = await catalogClient.GetEventAsync(cancellationToken);
        if (eventDocument is null)
        {
            return NotFound(new { message = "Event catalog is not configured." });
        }

        return Ok(new
        {
            id = eventDocument.Id,
            name = eventDocument.Name,
            venue = eventDocument.Venue,
            eventDateUtc = eventDocument.EventDateUtc,
            onSaleAtUtc = eventDocument.OnSaleAtUtc
        });
    }

    [HttpGet("tiers")]
    public async Task<IActionResult> GetTiers(CancellationToken cancellationToken)
    {
        var tiers = await catalogClient.GetTicketTiersAsync(cancellationToken);
        return Ok(tiers.Select(t => new
        {
            tier = t.Tier.ToString(),
            displayName = t.DisplayName,
            price = t.Price,
            capacity = t.Capacity
        }));
    }

    [HttpGet("seats")]
    public async Task<IActionResult> GetSeats([FromQuery] Tier? tier, CancellationToken cancellationToken)
    {
        var eventDocument = await catalogClient.GetEventAsync(cancellationToken);
        if (eventDocument is null || DateTime.UtcNow < eventDocument.OnSaleAtUtc)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Seats are not on sale yet." });
        }

        var seats = await seatRepository.GetByTierAsync(tier, cancellationToken);
        return Ok(seats.Select(s => new
        {
            id = s.Id,
            tier = s.Tier.ToString(),
            sectionLabel = s.SectionLabel,
            status = s.Status.ToString()
        }));
    }
}
