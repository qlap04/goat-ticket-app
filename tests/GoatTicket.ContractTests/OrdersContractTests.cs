using System.Net;
using System.Net.Http.Json;
using GoatTicket.Api.Contracts;
using GoatTicket.Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GoatTicket.ContractTests;

/// <summary>
/// T026: contract shape for POST /api/orders (201/402/409). No SeatHoldProcessor Function runs
/// in this test host, so granted holds are seeded directly through the repositories rather than
/// via the real queue path — this test is scoped to OrdersController's contract, not the full
/// hold pipeline (that's T029's job).
/// </summary>
[Collection("Api")]
public class OrdersContractTests(GoatTicketApiFactory factory)
{
    [Fact]
    public async Task Checkout_UnknownHoldRequestId_ReturnsNotFound()
    {
        var client = factory.CreateAuthenticatedClient("fan-orders-1");

        var response = await client.PostAsJsonAsync("/api/orders", new CheckoutInput([Guid.NewGuid()], new MockPaymentInput("succeed")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Checkout_HoldStillPending_ReturnsConflict()
    {
        const string userId = "fan-orders-2";
        var client = factory.CreateAuthenticatedClient(userId);
        var seats = await client.GetFromJsonAsync<List<SeatDto>>("/api/catalog/seats?tier=Standard");
        var seatId = seats!.Skip(2).First().id;
        var holdResponse = await client.PostAsJsonAsync("/api/cart/hold", new HoldRequestInput(seatId));
        var accepted = await holdResponse.Content.ReadFromJsonAsync<HoldAcceptedDto>();

        var response = await client.PostAsJsonAsync("/api/orders", new CheckoutInput([accepted!.requestId], new MockPaymentInput("succeed")));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Checkout_DeclinedPayment_ReleasesHoldAndReturns402()
    {
        const string userId = "fan-orders-3";
        var (client, holdRequestId, seatId) = await SeedGrantedHoldAsync(userId, tierIndex: 3);

        var response = await client.PostAsJsonAsync("/api/orders", new CheckoutInput([holdRequestId], new MockPaymentInput("decline")));

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var seatRepository = scope.ServiceProvider.GetRequiredService<SeatRepository>();
        var seat = await seatRepository.GetByIdAsync(seatId);
        Assert.Equal(GoatTicket.Domain.Entities.SeatStatus.Available, seat!.Status); // FR-015
    }

    [Fact]
    public async Task Checkout_SuccessfulPayment_CreatesOrderAndReturns201()
    {
        const string userId = "fan-orders-4";
        var (client, holdRequestId, seatId) = await SeedGrantedHoldAsync(userId, tierIndex: 4);

        var response = await client.PostAsJsonAsync("/api/orders", new CheckoutInput([holdRequestId], new MockPaymentInput("succeed")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<OrderDto>();
        Assert.NotNull(order);
        Assert.Equal("PaymentSucceeded", order!.status);
        Assert.Single(order.items);
        Assert.Equal(seatId, order.items[0].seatId);
    }

    /// <summary>Bypasses the queue to directly grant a hold, isolating OrdersController's contract from the async hold pipeline.</summary>
    private async Task<(HttpClient Client, Guid HoldRequestId, long SeatId)> SeedGrantedHoldAsync(string userId, int tierIndex)
    {
        var client = factory.CreateAuthenticatedClient(userId);
        var seats = await client.GetFromJsonAsync<List<SeatDto>>("/api/catalog/seats?tier=Standard");
        var seatId = seats!.Skip(tierIndex + 10).First().id; // offset to avoid clashing with other tests' seats

        using var scope = factory.Services.CreateScope();
        var seatRepository = scope.ServiceProvider.GetRequiredService<SeatRepository>();
        var holdRequestRepository = scope.ServiceProvider.GetRequiredService<HoldRequestRepository>();

        var holdRequest = await holdRequestRepository.CreateAsync(seatId, userId);
        var grantResult = await seatRepository.TryGrantHoldAsync(seatId, userId, TimeSpan.FromMinutes(10));
        Assert.Equal(HoldGrantResult.Granted, grantResult);
        await holdRequestRepository.SetStatusAsync(holdRequest.Id, GoatTicket.Domain.Entities.HoldRequestStatus.Success, null);

        return (client, holdRequest.Id, seatId);
    }

    private record SeatDto(long id, string tier, string sectionLabel, string status);
    private record HoldAcceptedDto(Guid requestId);
    private record OrderItemDto(long seatId, string tier, decimal price, string? ticketBlobUrl);
    private record OrderDto(Guid id, string userId, string status, decimal totalAmount, List<OrderItemDto> items, DateTime createdAtUtc);
}
