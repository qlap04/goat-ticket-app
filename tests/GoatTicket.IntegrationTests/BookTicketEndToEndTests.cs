using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using GoatTicket.Api.Contracts;
using GoatTicket.ContractTests;
using GoatTicket.Domain.Entities;
using GoatTicket.Functions;
using GoatTicket.Infrastructure.Catalog;
using GoatTicket.Infrastructure.Repositories;
using GoatTicket.Shared.Messages;
using GoatTicket.Shared.Ticketing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using LicenseType = QuestPDF.Infrastructure.LicenseType;

namespace GoatTicket.IntegrationTests;

/// <summary>
/// T029: the full checkout flow from spec.md User Story 1's four acceptance scenarios, against
/// real SQL Server/Cosmos/Azurite (via the shared <see cref="GoatTicketApiFactory"/>) and a
/// dedicated MailHog container to verify the e-ticket email actually arrives (quickstart.md
/// step 4). No live Azure Functions host runs in-test; SeatHoldProcessor and
/// OrderFulfillmentProcessor are invoked directly against messages read off the real Azurite
/// queues, which exercises the same code the isolated-worker runtime would call.
/// </summary>
public class BookTicketEndToEndTests(GoatTicketApiFactory factory) : IClassFixture<GoatTicketApiFactory>, IAsyncLifetime
{
    // Functions/Program.cs sets this, but that host never actually runs in this test — see the
    // class remarks above — so OrderFulfillmentProcessor's QuestPDF call would otherwise hit an
    // unlicensed-library exception.
    static BookTicketEndToEndTests() => QuestPDF.Settings.License = LicenseType.Community;

    private readonly IContainer _mailhog = new ContainerBuilder()
        .WithImage("mailhog/mailhog:latest")
        .WithPortBinding(8025, true)
        .WithPortBinding(1025, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/api/v2/messages")))
        .Build();

    public Task InitializeAsync() => _mailhog.StartAsync();

    public Task DisposeAsync() => _mailhog.DisposeAsync().AsTask();

    [Fact]
    public async Task FullPurchaseFlow_HoldCheckoutFulfillment_DeliversEticket()
    {
        const string userId = "fan-e2e-1";
        var client = factory.CreateAuthenticatedClient(userId);

        // 1. Browse (FR-002)
        var seats = await client.GetFromJsonAsync<List<SeatDto>>("/api/catalog/seats?tier=VIP");
        var seatId = seats!.First().id;

        // 2. Hold (FR-003, FR-007) — Acceptance Scenario 1 & 4
        var holdResponse = await client.PostAsJsonAsync("/api/cart/hold", new HoldRequestInput(seatId));
        Assert.Equal(HttpStatusCode.Accepted, holdResponse.StatusCode);
        var accepted = (await holdResponse.Content.ReadFromJsonAsync<HoldAcceptedDto>())!;

        await ProcessOneQueueMessageAsync("seat-hold-queue", ProcessSeatHoldMessageAsync);

        // 3. Poll for hold outcome (FR-008) — Acceptance Scenario 4
        var statusResponse = await client.GetAsync($"/api/cart/status/{accepted.requestId}");
        var status = (await statusResponse.Content.ReadFromJsonAsync<HoldStatusDto>())!;
        Assert.Equal("Success", status.status);

        // 4. Checkout (FR-009 - FR-012) — Acceptance Scenario 2
        var checkoutResponse = await client.PostAsJsonAsync("/api/orders", new CheckoutInput([accepted.requestId], new MockPaymentInput("succeed")));
        Assert.Equal(HttpStatusCode.Created, checkoutResponse.StatusCode);
        var order = (await checkoutResponse.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.Equal("PaymentSucceeded", order.status);

        await ProcessOneQueueMessageAsync("order-created", ProcessOrderCreatedMessageAsync);

        // 5. Verify fulfillment (FR-013, FR-013a, FR-014) — Acceptance Scenario 3
        using var scope = factory.Services.CreateScope();
        var orderRepository = scope.ServiceProvider.GetRequiredService<OrderRepository>();
        var finalOrder = await orderRepository.GetByIdAsync(order.id);
        Assert.Equal(OrderStatus.Fulfilled, finalOrder!.Status);
        Assert.NotNull(finalOrder.Items.Single().TicketBlobUrl);
        Assert.NotNull(finalOrder.Items.Single().QrPayloadJti);

        var mailhogBody = await GetMailHogMessagesJsonAsync();
        Assert.Contains($"{userId}@test.local", mailhogBody, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ProcessOneQueueMessageAsync(string queueName, Func<BinaryData, Task> handle)
    {
        var queueClient = new QueueServiceClient(factory.AzuriteConnectionString, new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 })
            .GetQueueClient(queueName);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var messages = await queueClient.ReceiveMessagesAsync(maxMessages: 1);
            var message = messages.Value.FirstOrDefault();
            if (message is not null)
            {
                await handle(message.Body);
                await queueClient.DeleteMessageAsync(message.MessageId, message.PopReceipt);
                return;
            }

            await Task.Delay(250);
        }

        Assert.Fail($"No message appeared on queue '{queueName}' within the wait window.");
    }

    private async Task ProcessSeatHoldMessageAsync(BinaryData body)
    {
        var message = JsonSerializer.Deserialize<SeatHoldQueueMessage>(body)!;
        using var scope = factory.Services.CreateScope();
        var processor = new SeatHoldProcessor(
            scope.ServiceProvider.GetRequiredService<SeatRepository>(),
            scope.ServiceProvider.GetRequiredService<HoldRequestRepository>(),
            scope.ServiceProvider.GetRequiredService<CosmosCatalogClient>(),
            NullLogger<SeatHoldProcessor>.Instance);

        await processor.RunAsync(message);
    }

    private async Task ProcessOrderCreatedMessageAsync(BinaryData body)
    {
        var message = JsonSerializer.Deserialize<OrderCreatedQueueMessage>(body)!;
        using var scope = factory.Services.CreateScope();

        using var rsa = RSA.Create(2048);
        var signer = TicketQrSigner.FromPrivateKeyPem(rsa.ExportRSAPrivateKeyPem());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:TicketsContainerName"] = "tickets",
                ["Email:SmtpHost"] = _mailhog.Hostname,
                ["Email:SmtpPort"] = _mailhog.GetMappedPublicPort(1025).ToString(),
                ["Email:FromAddress"] = "tickets@goat-ticket.local"
            })
            .Build();

        var processor = new OrderFulfillmentProcessor(
            scope.ServiceProvider.GetRequiredService<OrderRepository>(),
            scope.ServiceProvider.GetRequiredService<UserRepository>(),
            scope.ServiceProvider.GetRequiredService<BlobServiceClient>(),
            signer,
            configuration,
            NullLogger<OrderFulfillmentProcessor>.Instance);

        await processor.RunAsync(message);
    }

    private async Task<string> GetMailHogMessagesJsonAsync()
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri($"http://{_mailhog.Hostname}:{_mailhog.GetMappedPublicPort(8025)}") };
        return await httpClient.GetStringAsync("/api/v2/messages");
    }

    private record SeatDto(long id, string tier, string sectionLabel, string status);
    private record HoldAcceptedDto(Guid requestId);
    private record HoldStatusDto(Guid requestId, long seatId, string status, string? failureReason, DateTime? holdExpiresAtUtc);
    private record OrderItemDto(long seatId, string tier, decimal price, string? ticketBlobUrl);
    private record OrderDto(Guid id, string userId, string status, decimal totalAmount, List<OrderItemDto> items, DateTime createdAtUtc);
}
