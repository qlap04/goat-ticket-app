using System.Text.Json.Serialization;

namespace GoatTicket.Domain.Catalog;

/// <summary>The single "1000th Goal" ceremony document in the Cosmos `catalog` container (partition key `/type`).</summary>
public class EventDocument
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "event";

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("venue")]
    public required string Venue { get; set; }

    [JsonPropertyName("eventDateUtc")]
    public DateTime EventDateUtc { get; set; }

    [JsonPropertyName("onSaleAtUtc")]
    public DateTime OnSaleAtUtc { get; set; }
}
