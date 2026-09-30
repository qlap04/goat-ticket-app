using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace GoatTicket.ContractTests;

/// <summary>T041 (US3): contract shape + authorization for GET /api/admin/orders and /revenue-by-tier (FR-020).</summary>
[Collection("Api")]
public class AdminContractTests(GoatTicketApiFactory factory)
{
    [Fact]
    public async Task GetOrders_AsAdmin_ReturnsOrderList()
    {
        var client = factory.CreateAuthenticatedClient("admin-1", isAdmin: true);

        var response = await client.GetAsync("/api/admin/orders");

        response.EnsureSuccessStatusCode();
        var orders = await response.Content.ReadFromJsonAsync<List<object>>();
        Assert.NotNull(orders);
    }

    [Fact]
    public async Task GetRevenueByTier_AsAdmin_ReturnsTierBreakdown()
    {
        var client = factory.CreateAuthenticatedClient("admin-2", isAdmin: true);

        var response = await client.GetAsync("/api/admin/revenue-by-tier");

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task GetOrders_AsNonAdmin_ReturnsForbidden()
    {
        var client = factory.CreateAuthenticatedClient("fan-not-admin");

        var response = await client.GetAsync("/api/admin/orders"); // FR-020, spec.md US3 Acceptance Scenario 3

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetRevenueByTier_AsNonAdmin_ReturnsForbidden()
    {
        var client = factory.CreateAuthenticatedClient("fan-not-admin-2");

        var response = await client.GetAsync("/api/admin/revenue-by-tier");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
