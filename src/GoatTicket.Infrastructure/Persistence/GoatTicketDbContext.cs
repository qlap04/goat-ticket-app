using GoatTicket.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GoatTicket.Infrastructure.Persistence;

public class GoatTicketDbContext(DbContextOptions<GoatTicketDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<HoldRequest> HoldRequests => Set<HoldRequest>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(b =>
        {
            b.HasKey(u => u.Id);
            b.Property(u => u.Id).HasMaxLength(64);
            b.Property(u => u.Email).IsRequired().HasMaxLength(320);
            b.Property(u => u.DisplayName).IsRequired().HasMaxLength(256);
        });

        modelBuilder.Entity<Seat>(b =>
        {
            b.HasKey(s => s.Id);
            // Seat.Id is application-assigned (seed data + tests set explicit ids), not DB-generated.
            b.Property(s => s.Id).ValueGeneratedNever();
            b.Property(s => s.SectionLabel).IsRequired().HasMaxLength(128);
            b.Property(s => s.Tier).HasConversion<string>().HasMaxLength(16);
            b.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
            // The optimistic-concurrency token that is the actual mechanism enforcing
            // exactly-one-hold-per-seat (research.md §1) — not the queue's batchSize alone.
            b.Property(s => s.RowVersion).IsRowVersion();
            b.HasOne<User>()
                .WithMany()
                .HasForeignKey(s => s.HeldByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<HoldRequest>(b =>
        {
            b.HasKey(h => h.Id);
            b.Property(h => h.Status).HasConversion<string>().HasMaxLength(16);
            b.Property(h => h.FailureReason).HasMaxLength(256);
            b.HasOne<Seat>()
                .WithMany()
                .HasForeignKey(h => h.SeatId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>()
                .WithMany()
                .HasForeignKey(h => h.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Order>(b =>
        {
            b.HasKey(o => o.Id);
            b.Property(o => o.Status).HasConversion<string>().HasMaxLength(24);
            b.Property(o => o.TotalAmount).HasColumnType("decimal(10,2)");
            b.HasOne<User>()
                .WithMany()
                .HasForeignKey(o => o.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasMany(o => o.Items)
                .WithOne()
                .HasForeignKey(i => i.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrderItem>(b =>
        {
            b.HasKey(i => i.Id);
            b.Property(i => i.Tier).HasConversion<string>().HasMaxLength(16);
            b.Property(i => i.Price).HasColumnType("decimal(10,2)");
            b.Property(i => i.TicketBlobUrl).HasMaxLength(1024);
            b.Property(i => i.QrPayloadJti).HasMaxLength(64);
            b.HasOne<Seat>()
                .WithMany()
                .HasForeignKey(i => i.SeatId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
