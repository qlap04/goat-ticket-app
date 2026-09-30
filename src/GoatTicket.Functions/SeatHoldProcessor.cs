using GoatTicket.Domain.Entities;
using GoatTicket.Infrastructure.Catalog;
using GoatTicket.Infrastructure.Repositories;
using GoatTicket.Shared.Messages;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GoatTicket.Functions;

/// <summary>
/// Queue trigger on `seat-hold-queue` (host.json `batchSize = 1`). Per
/// contracts/queue-messages.md's consumer contract: the actual exactly-one-hold-per-seat
/// guarantee comes from <see cref="SeatRepository.TryGrantHoldAsync"/>'s RowVersion optimistic
/// concurrency check (research.md §1), not from batchSize alone — this Function stays correct
/// even scaled out across many instances during the on-sale-moment spike (FR-002a).
///
/// <c>Connection = "QueueStorage"</c> must resolve to the SAME storage account as
/// `Storage:AccountUri`/`Storage:LocalDevConnectionString` used by the Api project's
/// <c>QueueServiceClient</c> (AzureClients.CreateQueueServiceClient) — otherwise enqueued
/// messages would never reach this trigger. In production this is an identity-based connection
/// (`QueueStorage__queueServiceUri` + managed identity, constitution Principle III); locally it
/// is Azurite (`UseDevelopmentStorage=true`).
/// </summary>
public class SeatHoldProcessor(
    SeatRepository seatRepository,
    HoldRequestRepository holdRequestRepository,
    CosmosCatalogClient catalogClient,
    ILogger<SeatHoldProcessor> logger)
{
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(10);

    [Function(nameof(SeatHoldProcessor))]
    public async Task RunAsync([QueueTrigger("seat-hold-queue", Connection = "QueueStorage")] SeatHoldQueueMessage message)
    {
        var eventDocument = await catalogClient.GetEventAsync();
        if (eventDocument is null || DateTime.UtcNow < eventDocument.OnSaleAtUtc)
        {
            await holdRequestRepository.SetStatusAsync(message.HoldRequestId, HoldRequestStatus.Failed, "on-sale not yet open");
            logger.LogInformation("Hold {HoldRequestId} rejected for seat {SeatId}: on-sale not yet open", message.HoldRequestId, message.SeatId);
            return;
        }

        var result = await seatRepository.TryGrantHoldAsync(message.SeatId, message.UserId, HoldDuration);

        if (result == HoldGrantResult.Granted)
        {
            await holdRequestRepository.SetStatusAsync(message.HoldRequestId, HoldRequestStatus.Success, failureReason: null);
            logger.LogInformation("Hold {HoldRequestId} granted: seat {SeatId} -> user {UserId}", message.HoldRequestId, message.SeatId, message.UserId);
        }
        else
        {
            await holdRequestRepository.SetStatusAsync(message.HoldRequestId, HoldRequestStatus.Failed, "seat no longer available");
            logger.LogInformation("Hold {HoldRequestId} failed for seat {SeatId}: seat no longer available", message.HoldRequestId, message.SeatId);
        }
    }
}
