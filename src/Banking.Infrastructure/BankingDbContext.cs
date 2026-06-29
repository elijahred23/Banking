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
    public DbSet<WireCase> WireCases => Set<WireCase>();
    public DbSet<MessageDelivery> MessageDeliveries => Set<MessageDelivery>();
    public DbSet<FedSettlement> FedSettlements => Set<FedSettlement>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<CorrespondentRelationship> CorrespondentRelationships => Set<CorrespondentRelationship>();
    public DbSet<PaymentRoute> PaymentRoutes => Set<PaymentRoute>();
    public DbSet<PaymentRouteStep> PaymentRouteSteps => Set<PaymentRouteStep>();
    public DbSet<AchFile> AchFiles => Set<AchFile>();
    public DbSet<AchBatch> AchBatches => Set<AchBatch>();
    public DbSet<AchEntry> AchEntries => Set<AchEntry>();
    public DbSet<AchReturn> AchReturns => Set<AchReturn>();
    public DbSet<AchNotificationOfChange> AchNotificationsOfChange => Set<AchNotificationOfChange>();
    public DbSet<AchLedgerEntry> AchLedgerEntries => Set<AchLedgerEntry>();
    public DbSet<CheckDeposit> CheckDeposits => Set<CheckDeposit>();
    public DbSet<CheckImage> CheckImages => Set<CheckImage>();
    public DbSet<CheckCashLetter> CheckCashLetters => Set<CheckCashLetter>();
    public DbSet<CheckReturn> CheckReturns => Set<CheckReturn>();
    public DbSet<CheckLedgerEntry> CheckLedgerEntries => Set<CheckLedgerEntry>();
    public DbSet<CheckEvent> CheckEvents => Set<CheckEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Bank>().Property(x => x.MasterAccountBalance).HasPrecision(19, 4);
        modelBuilder.Entity<Account>().Property(x => x.Balance).HasPrecision(19, 4);
        modelBuilder.Entity<Account>().Property(x => x.HeldBalance).HasPrecision(19, 4);
        modelBuilder.Entity<Account>().Property(x => x.Balance).IsConcurrencyToken();
        modelBuilder.Entity<Account>().Property(x => x.HeldBalance).IsConcurrencyToken();
        modelBuilder.Entity<WireTransfer>().Property(x => x.Amount).HasPrecision(19, 4);
        modelBuilder.Entity<Bank>().HasIndex(x => x.RoutingNumber).IsUnique();
        modelBuilder.Entity<Bank>().HasIndex(x => x.Bic).IsUnique();
        modelBuilder.Entity<Account>().HasIndex(x => x.AccountNumber).IsUnique();
        modelBuilder.Entity<WireTransfer>().HasIndex(x => x.CorrelationId);
        modelBuilder.Entity<FedSettlement>().HasIndex(x => x.CorrelationId).IsUnique();
        modelBuilder.Entity<WireTransfer>().Property(x => x.Direction).HasConversion<string>();
        modelBuilder.Entity<WireTransfer>().Property(x => x.Status).HasConversion<string>();
        modelBuilder.Entity<IsoMessage>().Property(x => x.Direction).HasConversion<string>();
        modelBuilder.Entity<MessageDelivery>().Property(x => x.Status).HasConversion<string>();
        modelBuilder.Entity<WireTransfer>().Property(x => x.Scenario).HasConversion<string>();
        modelBuilder.Entity<WireTransfer>().Property(x => x.Rail).HasConversion<string>();
        modelBuilder.Entity<WireTransfer>().Property(x => x.TransferType).HasConversion<string>();
        modelBuilder.Entity<WireCase>().Property(x => x.Type).HasConversion<string>();
        modelBuilder.Entity<WireCase>().Property(x => x.Status).HasConversion<string>();
        modelBuilder.Entity<WireCase>().HasIndex(x => new { x.WireTransferId, x.CreatedDate });
        modelBuilder.Entity<WireCase>().HasOne(x => x.WireTransfer).WithMany(x => x.Cases)
            .HasForeignKey(x => x.WireTransferId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<WireCase>().HasOne<Bank>().WithMany()
            .HasForeignKey(x => x.RequestedByBankId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<LedgerEntry>().Property(x => x.Debit).HasPrecision(19, 4);
        modelBuilder.Entity<LedgerEntry>().Property(x => x.Credit).HasPrecision(19, 4);
        modelBuilder.Entity<LedgerEntry>().HasIndex(x => x.WireTransferId);
        modelBuilder.Entity<LedgerEntry>().HasIndex(x => x.JournalId);
        modelBuilder.Entity<WireTransfer>().HasOne(x => x.Bank).WithMany().HasForeignKey(x => x.BankId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<CorrespondentRelationship>().HasIndex(x => new
            { x.FromBankId, x.ToBankId, x.CurrencyCode, x.Rail }).IsUnique();
        modelBuilder.Entity<CorrespondentRelationship>().HasOne(x => x.FromBank).WithMany()
            .HasForeignKey(x => x.FromBankId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<CorrespondentRelationship>().HasOne(x => x.ToBank).WithMany()
            .HasForeignKey(x => x.ToBankId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PaymentRoute>().HasIndex(x => x.PaymentId).IsUnique();
        modelBuilder.Entity<PaymentRoute>().HasOne(x => x.Payment).WithOne(x => x.PaymentRoute)
            .HasForeignKey<PaymentRoute>(x => x.PaymentId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PaymentRoute>().HasOne(x => x.OriginBank).WithMany()
            .HasForeignKey(x => x.OriginBankId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PaymentRoute>().HasOne(x => x.DestinationBank).WithMany()
            .HasForeignKey(x => x.DestinationBankId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PaymentRouteStep>().HasIndex(x => new { x.PaymentRouteId, x.StepNumber }).IsUnique();
        modelBuilder.Entity<PaymentRouteStep>().HasIndex(x => x.MessageId)
            .HasFilter("[MessageId] IS NOT NULL");
        modelBuilder.Entity<PaymentRouteStep>().HasOne(x => x.PaymentRoute).WithMany(x => x.Steps)
            .HasForeignKey(x => x.PaymentRouteId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PaymentRouteStep>().HasOne(x => x.FromBank).WithMany()
            .HasForeignKey(x => x.FromBankId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PaymentRouteStep>().HasOne(x => x.ToBank).WithMany()
            .HasForeignKey(x => x.ToBankId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AchEntry>().Property(x => x.Amount).HasPrecision(19, 4);
        modelBuilder.Entity<AchEntry>().Property(x => x.SecCode).HasConversion<string>();
        modelBuilder.Entity<AchEntry>().Property(x => x.TransactionCode).HasConversion<string>();
        modelBuilder.Entity<AchEntry>().Property(x => x.Status).HasConversion<string>();
        modelBuilder.Entity<AchEntry>().Property(x => x.Purpose).HasConversion<string>();
        modelBuilder.Entity<AchEntry>().Property(x => x.Scenario).HasConversion<string>();
        modelBuilder.Entity<AchBatch>().Property(x => x.SecCode).HasConversion<string>();
        modelBuilder.Entity<AchEntry>().HasIndex(x => x.TraceNumber);
        modelBuilder.Entity<AchEntry>().HasIndex(x => x.ReceivingRoutingNumber);
        modelBuilder.Entity<AchBatch>().HasIndex(x => x.EffectiveEntryDate);
        modelBuilder.Entity<AchFile>().HasIndex(x => x.CreatedDate);
        modelBuilder.Entity<AchEntry>().HasOne(x => x.OriginatingBank).WithMany()
            .HasForeignKey(x => x.OriginatingBankId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AchEntry>().HasOne(x => x.ReceivingBank).WithMany()
            .HasForeignKey(x => x.ReceivingBankId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AchEntry>().HasOne(x => x.OriginatingAccount).WithMany()
            .HasForeignKey(x => x.OriginatingAccountId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AchBatch>().HasOne(x => x.OriginatingBank).WithMany()
            .HasForeignKey(x => x.OriginatingBankId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AchFile>().HasOne(x => x.OriginatingBank).WithMany()
            .HasForeignKey(x => x.OriginatingBankId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AchLedgerEntry>().Property(x => x.Debit).HasPrecision(19, 4);
        modelBuilder.Entity<AchLedgerEntry>().Property(x => x.Credit).HasPrecision(19, 4);
        modelBuilder.Entity<AchLedgerEntry>().HasIndex(x => x.AchEntryId);
        modelBuilder.Entity<AchLedgerEntry>().HasIndex(x => x.JournalId);

        modelBuilder.Entity<CheckDeposit>().Property(x => x.Amount).HasPrecision(19, 4);
        modelBuilder.Entity<CheckDeposit>().Property(x => x.Status).HasConversion<string>();
        modelBuilder.Entity<CheckDeposit>().Property(x => x.Scenario).HasConversion<string>();
        modelBuilder.Entity<CheckImage>().Property(x => x.Side).HasConversion<string>().HasMaxLength(10);
        modelBuilder.Entity<CheckImage>().Property(x => x.Format).HasConversion<string>().HasMaxLength(10);
        modelBuilder.Entity<CheckDeposit>().HasIndex(x => x.CorrelationId).IsUnique();
        modelBuilder.Entity<CheckDeposit>().HasIndex(x => x.PayingRoutingNumber);
        modelBuilder.Entity<CheckDeposit>().HasIndex(x => new
            { x.PayingRoutingNumber, x.PayingAccountNumber, x.CheckNumber, x.Amount });
        modelBuilder.Entity<CheckImage>().HasIndex(x => new { x.CheckDepositId, x.Side }).IsUnique();
        modelBuilder.Entity<CheckCashLetter>().HasIndex(x => x.CreatedDate);
        modelBuilder.Entity<CheckReturn>().HasIndex(x => x.CheckDepositId);
        modelBuilder.Entity<CheckLedgerEntry>().Property(x => x.Debit).HasPrecision(19, 4);
        modelBuilder.Entity<CheckLedgerEntry>().Property(x => x.Credit).HasPrecision(19, 4);
        modelBuilder.Entity<CheckLedgerEntry>().HasIndex(x => x.CheckDepositId);
        modelBuilder.Entity<CheckLedgerEntry>().HasIndex(x => x.JournalId);
        modelBuilder.Entity<CheckEvent>().HasIndex(x => x.CheckDepositId);
        modelBuilder.Entity<CheckDeposit>().HasOne(x => x.DepositoryBank).WithMany()
            .HasForeignKey(x => x.DepositoryBankId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<CheckDeposit>().HasOne(x => x.PayingBank).WithMany()
            .HasForeignKey(x => x.PayingBankId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<CheckDeposit>().HasOne(x => x.DepositingAccount).WithMany()
            .HasForeignKey(x => x.DepositingAccountId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<CheckDeposit>().HasOne(x => x.CashLetter).WithMany(x => x.Deposits)
            .HasForeignKey(x => x.CheckCashLetterId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<CheckCashLetter>().HasOne(x => x.DepositoryBank).WithMany()
            .HasForeignKey(x => x.DepositoryBankId).OnDelete(DeleteBehavior.Restrict);
    }
}
