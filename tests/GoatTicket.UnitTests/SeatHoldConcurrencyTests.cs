using GoatTicket.Domain.Entities;
using GoatTicket.Infrastructure.Persistence;
using GoatTicket.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Xunit;

namespace GoatTicket.UnitTests;

/// <summary>
/// Constitution Principle II (NON-NEGOTIABLE, test-first): this test must exist and fail
/// before SeatRepository.TryGrantHoldAsync / SeatHoldProcessor exist (T016 -> T017). It proves
/// the exactly-one-hold-per-seat guarantee against a real SQL Server via Testcontainers, not a
/// mock, per research.md §7.
/// </summary>
public class SeatHoldConcurrencyTests : IAsyncLifetime
{
    // Pinned to 2022 explicitly: the default 2019-CU18 image crashes on startup (SIGABRT) under
    // QEMU emulation on Apple Silicon Docker hosts.
    private readonly MsSqlContainer _sqlContainer = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    public async Task InitializeAsync()
    {
        await _sqlContainer.StartAsync();
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _sqlContainer.DisposeAsync();

    private GoatTicketDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<GoatTicketDbContext>()
            .UseSqlServer(_sqlContainer.GetConnectionString())
            .Options;
        return new GoatTicketDbContext(options);
    }

    [Fact]
    public async Task TryGrantHoldAsync_ManyConcurrentRequests_ExactlyOneSucceeds()
    {
        var result = await RunConcurrentHoldAttemptsAsync(seatId: 1, concurrentRequests: 50);

        Assert.Equal(1, result.GrantedCount);
        Assert.Equal(49, result.UnavailableCount);
        Assert.Equal(SeatStatus.Held, result.FinalSeatStatus);
    }

    /// <summary>Extended to flash-sale scale for US2 (T037, FR-006, SC-002).</summary>
    [Fact]
    public async Task TryGrantHoldAsync_FlashSaleScale_ExactlyOneSucceeds()
    {
        var result = await RunConcurrentHoldAttemptsAsync(seatId: 2, concurrentRequests: 500);

        Assert.Equal(1, result.GrantedCount);
        Assert.Equal(499, result.UnavailableCount);
        Assert.Equal(SeatStatus.Held, result.FinalSeatStatus);
    }

    private async Task<(int GrantedCount, int UnavailableCount, SeatStatus FinalSeatStatus)> RunConcurrentHoldAttemptsAsync(
        long seatId,
        int concurrentRequests)
    {
        await using (var seedDb = CreateDbContext())
        {
            seedDb.Seats.Add(new Seat
            {
                Id = seatId,
                Tier = Tier.VIP,
                SectionLabel = $"Test Seat {seatId}",
                Status = SeatStatus.Available
            });
            // HeldByUserId has an FK to Users.Id, so every synthetic user attempting a hold
            // needs a real row here first.
            seedDb.Users.AddRange(Enumerable.Range(0, concurrentRequests).Select(i => new User
            {
                Id = $"user-{seatId}-{i}",
                Email = $"user-{seatId}-{i}@example.com",
                DisplayName = $"Test User {seatId}-{i}"
            }));
            await seedDb.SaveChangesAsync();
        }

        var tasks = Enumerable.Range(0, concurrentRequests).Select(async i =>
        {
            await using var db = CreateDbContext();
            var repository = new SeatRepository(db);
            return await repository.TryGrantHoldAsync(seatId, $"user-{seatId}-{i}", TimeSpan.FromMinutes(10));
        });

        var results = await Task.WhenAll(tasks);

        var grantedCount = results.Count(r => r == HoldGrantResult.Granted);
        var unavailableCount = results.Count(r => r == HoldGrantResult.SeatNotAvailable);

        await using var verifyDb = CreateDbContext();
        var finalSeat = await verifyDb.Seats.AsNoTracking().SingleAsync(s => s.Id == seatId);

        return (grantedCount, unavailableCount, finalSeat.Status);
    }
}
