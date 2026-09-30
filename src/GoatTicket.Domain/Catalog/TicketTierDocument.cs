using System.Text.Json.Serialization;
using GoatTicket.Domain.Entities;

namespace GoatTicket.Domain.Catalog;

/// <summary>One per ticket tier in the Cosmos `catalog` container (partition key `/type`).</summary>
public class TicketTierDocument
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "ticketTier";

    [JsonPropertyName("tier")]
    public Tier Tier { get; set; }

    [JsonPropertyName("displayName")]
    public required string DisplayName { get; set; }

    [JsonPropertyName("price")]
    public decimal Price { get; set; }

    [JsonPropertyName("capacity")]
    public int Capacity { get; set; }
}
