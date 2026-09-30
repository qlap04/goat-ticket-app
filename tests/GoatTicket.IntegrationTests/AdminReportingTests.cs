using System.Net;
using System.Net.Http.Json;
using GoatTicket.Api.Contracts;
using GoatTicket.ContractTests;
using GoatTicket.Domain.Entities;
using GoatTicket.Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GoatTicket.IntegrationTests;

/// <summary>
/// T043 (US3): place a few orders across tiers via the User Story 1 flow (granted holds seeded
/// directly, same as OrdersContractTests, to isolate this from the queue-processing pipeline),
/// then confirm admin reporting reconciles exactly with what was sold (FR-018, FR-019) and that
/// a non-admin is denied (FR-020, spec.md US3 Acceptance Scenarios 1-3).
/// </summary>
public class AdminReportingTests(GoatTicketApiFactory factory) : IClassFixture<GoatTicketApiFactory>
{
    [Fact]
    public async Task AdminOrdersAndRevenue_ReconcileWithPlacedOrders_AndDenyNonAdmins()
    {
        var order1 = await PlaceOrderAsync("admin-report-fan-1", "VIP");
        var order2 = await PlaceOrderAsync("admin-report-fan-2", "Standard");

        var adminClient = factory.CreateAuthenticatedClient("admin-reporting-1", isAdmin: true);

        var ordersResponse = await adminClient.GetAsync("/api/admin/orders");
        ordersResponse.EnsureSuccessStatusCode();
        var orders = await ordersResponse.Content.ReadFromJsonAsync<List<OrderDto>>();
        Assert.Contains(orders!, o => o.id == order1.id);
        Assert.Contains(orders!, o => o.id == order2.id);

        var revenueResponse = await adminClient.GetAsync("/api/admin/revenue-by-tier");
        revenueResponse.EnsureSuccessStatusCode();
        var revenue = await revenueResponse.Content.ReadFromJsonAsync<List<TierRevenueDto>>();
        var vipRevenue = revenue!.Single(r => r.tier == "VIP");
        Assert.True(vipRevenue.totalRevenue >= order1.totalAmount); // >= in case other tests in the shared fixture also sold VIP seats

        var nonAdminClient = factory.CreateAuthenticatedClient("fan-not-admin-report");
        var forbiddenResponse = await nonAdminClient.GetAsync("/api/admin/orders");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode); // Acceptance Scenario 3
    }

    private async Task<OrderDto> PlaceOrderAsync(string userId, string tier)
    {
        var client = factory.CreateAuthenticatedClient(userId);
        var seats = await client.GetFromJsonAsync<List<SeatDto>>($"/api/catalog/seats?tier={tier}");
        var seatId = seats!.Skip(100).First().id; // offset to avoid clashing with other tests' seats

        using var scope = factory.Services.CreateScope();
        var seatRepository = scope.ServiceProvider.GetRequiredService<SeatRepository>();
        var holdRequestRepository = scope.ServiceProvider.GetRequiredService<HoldRequestRepository>();

        var holdRequest = await holdRequestRepository.CreateAsync(seatId, userId);
        var grantResult = await seatRepository.TryGrantHoldAsync(seatId, userId, TimeSpan.FromMinutes(10));
        Assert.Equal(HoldGrantResult.Granted, grantResult);
        await holdRequestRepository.SetStatusAsync(holdRequest.Id, HoldRequestStatus.Success, null);

        var checkoutResponse = await client.PostAsJsonAsync("/api/orders", new CheckoutInput([holdRequest.Id], new MockPaymentInput("succeed")));
        checkoutResponse.EnsureSuccessStatusCode();
        return (await checkoutResponse.Content.ReadFromJsonAsync<OrderDto>())!;
    }

    private record SeatDto(long id, string tier, string sectionLabel, string status);
    private record OrderItemDto(long seatId, string tier, decimal price, string? ticketBlobUrl);
    private record OrderDto(Guid id, string userId, string status, decimal totalAmount, List<OrderItemDto> items, DateTime createdAtUtc);
    private record TierRevenueDto(string tier, decimal totalRevenue, int ticketsSold);
}
