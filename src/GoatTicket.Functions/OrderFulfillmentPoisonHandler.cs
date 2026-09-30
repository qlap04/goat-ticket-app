using GoatTicket.Domain.Entities;
using GoatTicket.Infrastructure.Repositories;
using GoatTicket.Shared.Messages;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GoatTicket.Functions;

/// <summary>
/// Queue trigger on `order-created-poison`, auto-populated by the Functions runtime once
/// <see cref="OrderFulfillmentProcessor"/> exhausts `host.json`'s `maxDequeueCount` retries.
/// Flags the order for manual follow-up (research.md §9) rather than letting the failure
/// vanish silently — an admin sees it via GET /admin/orders (FR-018).
/// </summary>
public class OrderFulfillmentPoisonHandler(OrderRepository orderRepository, ILogger<OrderFulfillmentPoisonHandler> logger)
{
    [Function(nameof(OrderFulfillmentPoisonHandler))]
    public async Task RunAsync([QueueTrigger("order-created-poison", Connection = "QueueStorage")] OrderCreatedQueueMessage message)
    {
        await orderRepository.SetStatusAsync(message.OrderId, OrderStatus.FulfillmentFailed);
        logger.LogCritical(
            "Order {OrderId} fulfillment exhausted all retries and requires manual follow-up.",
            message.OrderId);
    }
}
