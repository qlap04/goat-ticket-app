using System.Net;
using GoatTicket.Infrastructure.Catalog;
using Microsoft.Azure.Cosmos;
using Xunit;

namespace GoatTicket.UnitTests;

/// <summary>The emulator answers its first writes with a 408 on a loaded CI box; the retry must absorb that and nothing else.</summary>
public class CosmosRetryTests
{
    private static CosmosException Failure(HttpStatusCode status) => new("failed", status, 0, "activity", 0);

    [Fact]
    public async Task ExecuteAsync_RetriesATimeout_AndReturnsTheLaterResult()
    {
        var calls = 0;

        var result = await CosmosRetry.ExecuteAsync(
            () =>
            {
                calls++;
                return calls < 3 ? throw Failure(HttpStatusCode.RequestTimeout) : Task.FromResult(42);
            },
            initialDelay: TimeSpan.Zero);

        Assert.Equal(42, result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRetryABadRequest()
    {
        var calls = 0;

        await Assert.ThrowsAsync<CosmosException>(() => CosmosRetry.ExecuteAsync<int>(
            () =>
            {
                calls++;
                throw Failure(HttpStatusCode.BadRequest);
            },
            initialDelay: TimeSpan.Zero));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExecuteAsync_GivesUpAfterTheMaximumNumberOfAttempts()
    {
        var calls = 0;

        await Assert.ThrowsAsync<CosmosException>(() => CosmosRetry.ExecuteAsync<int>(
            () =>
            {
                calls++;
                throw Failure(HttpStatusCode.ServiceUnavailable);
            },
            maxAttempts: 3,
            initialDelay: TimeSpan.Zero));

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRetryACancellationTheCallerAskedFor()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var calls = 0;

        await Assert.ThrowsAsync<TaskCanceledException>(() => CosmosRetry.ExecuteAsync<int>(
            () =>
            {
                calls++;
                throw new TaskCanceledException();
            },
            cancellation.Token,
            initialDelay: TimeSpan.Zero));

        Assert.Equal(1, calls);
    }
}
