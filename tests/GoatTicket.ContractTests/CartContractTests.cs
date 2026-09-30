using System.Net;
using System.Net.Http.Json;
using GoatTicket.Api.Contracts;
using Xunit;

namespace GoatTicket.ContractTests;

/// <summary>
/// T025: contract shape for POST /api/cart/hold and GET /api/cart/status/{requestId}. No
/// SeatHoldProcessor Function is running in this test host, so the request stays `Pending`
/// forever — that's fine, this test only asserts the 202/response shape (FR-007, FR-008), not
/// end-to-end hold processing (that's covered by the integration test, T029).
/// </summary>
[Collection("Api")]
public class CartContractTests(GoatTicketApiFactory factory)
{
    [Fact]
    public async Task RequestHold_ReturnsAcceptedWithRequestId()
    {
        var client = factory.CreateAuthenticatedClient("fan-cart-1");
        var seats = await client.GetFromJsonAsync<List<SeatDto>>("/api/catalog/seats?tier=Standard");
        var seatId = seats!.First().id;

        var response = await client.PostAsJsonAsync("/api/cart/hold", new HoldRequestInput(seatId));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HoldAcceptedDto>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.requestId);
    }

    [Fact]
    public async Task GetStatus_UnknownRequestId_ReturnsNotFound()
    {
        var client = factory.CreateAuthenticatedClient("fan-cart-2");

        var response = await client.GetAsync($"/api/cart/status/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetStatus_KnownRequestId_ReturnsPendingShape()
    {
        var client = factory.CreateAuthenticatedClient("fan-cart-3");
        var seats = await client.GetFromJsonAsync<List<SeatDto>>("/api/catalog/seats?tier=Standard");
        var seatId = seats!.Skip(1).First().id;
        var holdResponse = await client.PostAsJsonAsync("/api/cart/hold", new HoldRequestInput(seatId));
        var accepted = await holdResponse.Content.ReadFromJsonAsync<HoldAcceptedDto>();

        var response = await client.GetAsync($"/api/cart/status/{accepted!.requestId}");

        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<HoldStatusDto>();
        Assert.NotNull(status);
        Assert.Equal("Pending", status!.status);
    }

    private record SeatDto(long id, string tier, string sectionLabel, string status);
    private record HoldAcceptedDto(Guid requestId);
    private record HoldStatusDto(Guid requestId, long seatId, string status, string? failureReason, DateTime? holdExpiresAtUtc);
}
