using System.Text.Json;
using Azure.Storage.Queues;
using GoatTicket.Api.Contracts;
using GoatTicket.Domain.Entities;
using GoatTicket.Infrastructure.Catalog;
using GoatTicket.Infrastructure.Repositories;
using GoatTicket.Shared.Messages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web;

namespace GoatTicket.Api.Controllers;

/// <summary>
/// FR-003, FR-006, FR-007, FR-008: enqueue a seat hold and poll for its outcome. This
/// controller never grants a hold itself — it only enqueues and reads status, per the
/// Queue-Based Load Leveling pattern (constitution Principle IV, research.md §3).
/// </summary>
[ApiController]
[Authorize]
[Route("api/cart")]
public class CartController(
    HoldRequestRepository holdRequestRepository,
    SeatRepository seatRepository,
    CosmosCatalogClient catalogClient,
    QueueServiceClient queueServiceClient,
    IConfiguration configuration,
    ILogger<CartController> logger) : ControllerBase
{
    [HttpPost("hold")]
    public async Task<IActionResult> RequestHold([FromBody] HoldRequestInput input, CancellationToken cancellationToken)
    {
        var userId = User.GetObjectId()
            ?? throw new InvalidOperationException("Authenticated request is missing an oid claim.");

        var eventDocument = await catalogClient.GetEventAsync(cancellationToken);
        if (eventDocument is null || DateTime.UtcNow < eventDocument.OnSaleAtUtc)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Seats are not on sale yet." });
        }

        var holdRequest = await holdRequestRepository.CreateAsync(input.SeatId, userId, cancellationToken);

        var queueName = configuration["Storage:SeatHoldQueueName"] ?? "seat-hold-queue";
        var queueClient = queueServiceClient.GetQueueClient(queueName);
        await queueClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var message = new SeatHoldQueueMessage(holdRequest.Id, input.SeatId, userId, DateTime.UtcNow);
        await queueClient.SendMessageAsync(JsonSerializer.Serialize(message), cancellationToken);

        logger.LogInformation("Hold {HoldRequestId} enqueued for seat {SeatId} by user {UserId}.", holdRequest.Id, input.SeatId, userId);
        return Accepted(new { requestId = holdRequest.Id });
    }

    [HttpGet("status/{requestId:guid}")]
    public async Task<IActionResult> GetStatus(Guid requestId, CancellationToken cancellationToken)
    {
        var holdRequest = await holdRequestRepository.GetByIdAsync(requestId, cancellationToken);
        if (holdRequest is null)
        {
            return NotFound(new { message = "Unknown requestId." });
        }

        Seat? seat = null;
        if (holdRequest.Status == HoldRequestStatus.Success)
        {
            seat = await seatRepository.GetByIdAsync(holdRequest.SeatId, cancellationToken);
        }

        return Ok(new
        {
            requestId = holdRequest.Id,
            seatId = holdRequest.SeatId,
            status = holdRequest.Status.ToString(),
            failureReason = holdRequest.FailureReason,
            holdExpiresAtUtc = seat?.HoldExpiresAtUtc
        });
    }
}
