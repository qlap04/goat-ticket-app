using Xunit;

namespace GoatTicket.ContractTests;

/// <summary>Shares one GoatTicketApiFactory (and its Testcontainers) across all contract test classes.</summary>
[CollectionDefinition("Api")]
public class ApiTestCollection : ICollectionFixture<GoatTicketApiFactory>;
