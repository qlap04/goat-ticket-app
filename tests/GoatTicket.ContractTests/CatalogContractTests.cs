using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace GoatTicket.ContractTests;

/// <summary>T024: contract shape for GET /api/catalog/event, /tiers, /seats (contracts/openapi.yaml).</summary>
[Collection("Api")]
public class CatalogContractTests(GoatTicketApiFactory factory)
{
    [Fact]
    public async Task GetEvent_ReturnsEventShape()
    {
        var client = factory.CreateAuthenticatedClient("fan-1");

        var response = await client.GetAsync("/api/catalog/event");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<EventDto>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrEmpty(body!.name));
    }

    [Fact]
    public async Task GetTiers_ReturnsAllFiveTiers()
    {
        var client = factory.CreateAuthenticatedClient("fan-1");

        var response = await client.GetAsync("/api/catalog/tiers");

        response.EnsureSuccessStatusCode();
        var tiers = await response.Content.ReadFromJsonAsync<List<TierDto>>();
        Assert.NotNull(tiers);
        Assert.Equal(5, tiers!.Count);
    }

    [Fact]
    public async Task GetSeats_OnSaleOpen_ReturnsAvailableSeats()
    {
        var client = factory.CreateAuthenticatedClient("fan-1");

        var response = await client.GetAsync("/api/catalog/seats?tier=VIP");

        response.EnsureSuccessStatusCode();
        var seats = await response.Content.ReadFromJsonAsync<List<SeatDto>>();
        Assert.NotNull(seats);
        Assert.NotEmpty(seats!);
        Assert.All(seats!, s => Assert.Equal("VIP", s.tier));
    }

    [Fact]
    public async Task GetSeats_Unauthenticated_ReturnsUnauthorized()
    {
        var client = factory.CreateClient(); // no X-Test-UserId header

        var response = await client.GetAsync("/api/catalog/seats");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private record EventDto(string id, string name, string venue, DateTime eventDateUtc, DateTime onSaleAtUtc);
    private record TierDto(string tier, string displayName, decimal price, int capacity);
    private record SeatDto(long id, string tier, string sectionLabel, string status);
}
