using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public sealed class BankingDbContext(DbContextOptions<BankingDbContext> options) : DbContext(options)
{
    public DbSet<Bank> Banks => Set<Bank>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<WireTransfer> WireTransfers => Set<WireTransfer>();
    public DbSet<IsoMessage> IsoMessages => Set<IsoMessage>();
    public DbSet<WireEvent> WireEvents => Set<WireEvent>();
    public DbSet<MessageDelivery> MessageDeliveries => Set<MessageDelivery>();
    public DbSet<FedSettlement> FedSettlements => Set<FedSettlement>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Bank>().Property(x => x.MasterAccountBalance).HasPrecision(19, 4);
        modelBuilder.Entity<Account>().Property(x => x.Balance).HasPrecision(19, 4);
        modelBuilder.Entity<Account>().Property(x => x.HeldBalance).HasPrecision(19, 4);
        modelBuilder.Entity<Account>().Property(x => x.Balance).IsConcurrencyToken();
        modelBuilder.Entity<Account>().Property(x => x.HeldBalance).IsConcurrencyToken();
        modelBuilder.Entity<WireTransfer>().Property(x => x.Amount).HasPrecision(19, 4);
        modelBuilder.Entity<Bank>().HasIndex(x => x.RoutingNumber).IsUnique();
        modelBuilder.Entity<Account>().HasIndex(x => x.AccountNumber).IsUnique();
        modelBuilder.Entity<WireTransfer>().HasIndex(x => x.CorrelationId);
        modelBuilder.Entity<FedSettlement>().HasIndex(x => x.CorrelationId).IsUnique();
        modelBuilder.Entity<WireTransfer>().Property(x => x.Direction).HasConversion<string>();
        modelBuilder.Entity<WireTransfer>().Property(x => x.Status).HasConversion<string>();
        modelBuilder.Entity<IsoMessage>().Property(x => x.Direction).HasConversion<string>();
        modelBuilder.Entity<MessageDelivery>().Property(x => x.Status).HasConversion<string>();
        modelBuilder.Entity<WireTransfer>().Property(x => x.Scenario).HasConversion<string>();
        modelBuilder.Entity<WireTransfer>().Property(x => x.Rail).HasConversion<string>();
        modelBuilder.Entity<LedgerEntry>().Property(x => x.Debit).HasPrecision(19, 4);
        modelBuilder.Entity<LedgerEntry>().Property(x => x.Credit).HasPrecision(19, 4);
        modelBuilder.Entity<LedgerEntry>().HasIndex(x => x.WireTransferId);
        modelBuilder.Entity<LedgerEntry>().HasIndex(x => x.JournalId);
        modelBuilder.Entity<WireTransfer>().HasOne(x => x.Bank).WithMany().HasForeignKey(x => x.BankId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
