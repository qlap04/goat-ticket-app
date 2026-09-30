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

/// <summary>FR-009 through FR-017: checkout one or more granted holds via the mocked payment gateway.</summary>
[ApiController]
[Authorize]
[Route("api/orders")]
public class OrdersController(
    HoldRequestRepository holdRequestRepository,
    OrderRepository orderRepository,
    SeatRepository seatRepository,
    CosmosCatalogClient catalogClient,
    QueueServiceClient queueServiceClient,
    IConfiguration configuration,
    ILogger<OrdersController> logger) : ControllerBase
{
    private static readonly string[] ValidSimulateValues = ["succeed", "decline"];

    [HttpPost]
    public async Task<IActionResult> Checkout([FromBody] CheckoutInput input, CancellationToken cancellationToken)
    {
        var userId = User.GetObjectId()
            ?? throw new InvalidOperationException("Authenticated request is missing an oid claim.");

        if (input.HoldRequestIds.Count == 0)
        {
            return BadRequest(new { message = "At least one holdRequestId is required." });
        }

        if (!ValidSimulateValues.Contains(input.Payment.Simulate, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = $"payment.simulate must be one of: {string.Join(", ", ValidSimulateValues)}." });
        }

        var holdRequests = new List<HoldRequest>();
        foreach (var holdRequestId in input.HoldRequestIds)
        {
            var holdRequest = await holdRequestRepository.GetByIdAsync(holdRequestId, cancellationToken);
            if (holdRequest is null || holdRequest.UserId != userId)
            {
                return NotFound(new { message = $"Unknown hold request {holdRequestId}." });
            }

            if (holdRequest.Status != HoldRequestStatus.Success)
            {
                return Conflict(new { message = $"Hold request {holdRequestId} was not granted." });
            }

            var seat = await seatRepository.GetByIdAsync(holdRequest.SeatId, cancellationToken);
            var stillValid = seat is not null
                && seat.Status == SeatStatus.Held
                && seat.HeldByUserId == userId
                && seat.HoldExpiresAtUtc is not null
                && seat.HoldExpiresAtUtc >= DateTime.UtcNow;

            if (!stillValid)
            {
                return Conflict(new { message = $"Hold for seat {holdRequest.SeatId} has expired or was already consumed." }); // FR-016
            }

            holdRequests.Add(holdRequest);
        }

        if (string.Equals(input.Payment.Simulate, "decline", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var holdRequest in holdRequests)
            {
                await seatRepository.ReleaseHeldSeatAsync(holdRequest.SeatId, cancellationToken); // FR-015
            }

            logger.LogInformation("Checkout declined for user {UserId}: {HoldCount} hold(s) released.", userId, holdRequests.Count);
            return StatusCode(StatusCodes.Status402PaymentRequired, new { message = "Payment declined." });
        }

        var tierPrices = (await catalogClient.GetTicketTiersAsync(cancellationToken))
            .ToDictionary(t => t.Tier, t => t.Price);

        var order = new Order
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Status = OrderStatus.PaymentSucceeded,
            CreatedAtUtc = DateTime.UtcNow
        };

        decimal total = 0m;
        foreach (var holdRequest in holdRequests)
        {
            var seat = await seatRepository.GetByIdAsync(holdRequest.SeatId, cancellationToken);
            var price = seat is not null && tierPrices.TryGetValue(seat.Tier, out var tierPrice) ? tierPrice : 0m;
            total += price;

            order.Items.Add(new OrderItem
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                SeatId = holdRequest.SeatId,
                Tier = seat!.Tier,
                Price = price
            });
        }

        order.TotalAmount = total;

        await orderRepository.CreateAsync(order, cancellationToken); // FR-012, FR-017

        foreach (var holdRequest in holdRequests)
        {
            await seatRepository.MarkSoldAsync(holdRequest.SeatId, cancellationToken);
        }

        var queueName = configuration["Storage:OrderCreatedQueueName"] ?? "order-created";
        var queueClient = queueServiceClient.GetQueueClient(queueName);
        await queueClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var message = new OrderCreatedQueueMessage(order.Id, DateTime.UtcNow);
        await queueClient.SendMessageAsync(JsonSerializer.Serialize(message), cancellationToken);

        logger.LogInformation("Order {OrderId} created for user {UserId}: {ItemCount} seat(s), total {Total:C}.", order.Id, userId, order.Items.Count, order.TotalAmount);
        return Created($"/api/orders/{order.Id}", order.ToDto());
    }
}
