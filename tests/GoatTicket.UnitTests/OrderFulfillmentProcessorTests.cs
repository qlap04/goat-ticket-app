using System.Security.Cryptography;
using System.Text;
using Azure.Storage.Blobs;
using GoatTicket.Domain.Entities;
using GoatTicket.Functions;
using GoatTicket.Infrastructure.Persistence;
using GoatTicket.Infrastructure.Repositories;
using GoatTicket.Shared.Messages;
using GoatTicket.Shared.Ticketing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.Azurite;
using Testcontainers.MsSql;
using Xunit;

namespace GoatTicket.UnitTests;

/// <summary>
/// T028: idempotency of OrderFulfillmentProcessor (research.md §9) — re-running the processor
/// on an order where the ticket was already generated and the email already sent must not
/// regenerate the PDF or send a second email. No SMTP server runs in this test, so if the code
/// incorrectly attempted to send despite EmailSentAtUtc already being set, it would throw and
/// fail the test — that absence is itself part of the assertion.
/// </summary>
public class OrderFulfillmentProcessorTests : IAsyncLifetime
{
    private const string ContainerName = "tickets";

    private readonly MsSqlContainer _sql = new MsSqlBuilder().Build();
    private readonly AzuriteContainer _azurite = new AzuriteBuilder().Build();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_sql.StartAsync(), _azurite.StartAsync());
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _sql.DisposeAsync();
        await _azurite.DisposeAsync();
    }

    private GoatTicketDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<GoatTicketDbContext>()
            .UseSqlServer(_sql.GetConnectionString())
            .Options;
        return new GoatTicketDbContext(options);
    }

    [Fact]
    public async Task RunAsync_ItemAlreadyGeneratedAndEmailAlreadySent_DoesNotRegenerateOrResend()
    {
        var blobServiceClient = new BlobServiceClient(_azurite.GetConnectionString());
        var containerClient = blobServiceClient.GetBlobContainerClient(ContainerName);
        await containerClient.CreateIfNotExistsAsync();

        const long seatId = 999L;
        var orderId = Guid.NewGuid();
        var blobClient = containerClient.GetBlobClient($"{orderId}/{seatId}.pdf");
        var sentinelBytes = Encoding.UTF8.GetBytes("SENTINEL-ALREADY-GENERATED");
        await using (var stream = new MemoryStream(sentinelBytes))
        {
            await blobClient.UploadAsync(stream, overwrite: true);
        }

        await using (var seedDb = CreateDbContext())
        {
            seedDb.Seats.Add(new Seat { Id = seatId, Tier = Tier.VIP, SectionLabel = "Test Seat", Status = SeatStatus.Sold });
            seedDb.Users.Add(new User { Id = "user-1", Email = "fan@example.com", DisplayName = "Fan", CreatedAtUtc = DateTime.UtcNow });
            seedDb.Orders.Add(new Order
            {
                Id = orderId,
                UserId = "user-1",
                Status = OrderStatus.PaymentSucceeded,
                TotalAmount = 100m,
                EmailSentAtUtc = DateTime.UtcNow, // already sent — must not send again
                CreatedAtUtc = DateTime.UtcNow,
                Items =
                [
                    new OrderItem
                    {
                        Id = Guid.NewGuid(),
                        OrderId = orderId,
                        SeatId = seatId,
                        Tier = Tier.VIP,
                        Price = 100m,
                        TicketBlobUrl = blobClient.Uri.ToString(),
                        QrPayloadJti = "existing-jti"
                    }
                ]
            });
            await seedDb.SaveChangesAsync();
        }

        using var rsa = RSA.Create(2048);
        var signer = TicketQrSigner.FromPrivateKeyPem(rsa.ExportRSAPrivateKeyPem());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:TicketsContainerName"] = ContainerName })
            .Build();

        await using var processorDb = CreateDbContext();
        var processor = new OrderFulfillmentProcessor(
            new OrderRepository(processorDb),
            new UserRepository(processorDb),
            blobServiceClient,
            signer,
            configuration,
            NullLogger<OrderFulfillmentProcessor>.Instance);

        await processor.RunAsync(new OrderCreatedQueueMessage(orderId, DateTime.UtcNow));

        var downloaded = await blobClient.DownloadContentAsync();
        Assert.Equal(sentinelBytes, downloaded.Value.Content.ToArray()); // blob untouched

        await using var verifyDb = CreateDbContext();
        var finalOrder = await verifyDb.Orders.Include(o => o.Items).AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatus.Fulfilled, finalOrder.Status);
        Assert.Equal("existing-jti", finalOrder.Items.Single().QrPayloadJti); // untouched
    }
}
