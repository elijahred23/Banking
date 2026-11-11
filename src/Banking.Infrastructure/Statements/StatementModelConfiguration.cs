using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

internal static class StatementModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<CorrespondentAccount>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<CorrespondentPaymentLink>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<CorrespondentLedgerEntry>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<AccountStatement>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<AccountStatementEntry>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<StatementSchedule>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<StatementDelivery>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<StatementReconciliation>().Property(x => x.Id).ValueGeneratedNever();
        var account = model.Entity<CorrespondentAccount>();
        account.Property(x => x.RowVersion).IsRowVersion();
        account.ToTable("CorrespondentAccounts", t => t.HasCheckConstraint("CK_CorrespondentAccounts_Ownership", "IssuerBankId <> RespondentBankId AND OpeningBalance >= 0 AND IssuerBalance >= 0 AND HeldBalance >= 0 AND HeldBalance <= IssuerBalance"));
        account.HasOne<Bank>().WithMany().HasForeignKey(x => x.IssuerBankId).OnDelete(DeleteBehavior.Restrict);
        account.HasOne<Bank>().WithMany().HasForeignKey(x => x.RespondentBankId).OnDelete(DeleteBehavior.Restrict);
        account.HasIndex(x => new { x.IssuerBankId, x.AccountNumber }).IsUnique();
        account.HasIndex(x => new { x.IssuerBankId, x.RespondentBankId }).IsUnique();
        var link = model.Entity<CorrespondentPaymentLink>();
        link.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        link.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        link.HasOne<WireTransfer>().WithMany().HasForeignKey(x => x.WireTransferId).OnDelete(DeleteBehavior.Restrict);
        link.HasIndex(x => x.WireTransferId).IsUnique();
        link.HasIndex(x => new { x.Status, x.CreatedAt });
        var ledger = model.Entity<CorrespondentLedgerEntry>();
        ledger.HasIndex(x => x.JournalId);
        ledger.ToTable("CorrespondentLedgerEntries", t => t.HasCheckConstraint("CK_CorrespondentLedgerEntries_OneSide", "(Debit > 0 AND Credit = 0) OR (Credit > 0 AND Debit = 0)"));
        ledger.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        ledger.HasOne<Bank>().WithMany().HasForeignKey(x => x.BankId).OnDelete(DeleteBehavior.Restrict);
        ledger.HasOne<WireTransfer>().WithMany().HasForeignKey(x => x.WireTransferId).OnDelete(DeleteBehavior.Restrict);
        ledger.HasIndex(x => new { x.BankId, x.PostingId, x.IsAccountLine }).IsUnique();
        ledger.HasIndex(x => new { x.AccountId, x.BankId, x.BookingAt });
        ledger.HasIndex(x => new { x.BankId, x.CorrelationId });
        var statement = model.Entity<AccountStatement>();
        statement.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        statement.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        statement.HasOne<Bank>().WithMany().HasForeignKey(x => x.IssuerBankId).OnDelete(DeleteBehavior.Restrict);
        statement.HasOne<Bank>().WithMany().HasForeignKey(x => x.RecipientBankId).OnDelete(DeleteBehavior.Restrict);
        statement.HasOne<AccountStatement>().WithMany().HasForeignKey(x => x.PreviousStatementId).OnDelete(DeleteBehavior.Restrict);
        statement.HasIndex(x => x.StatementNumber).IsUnique();
        statement.HasIndex(x => new { x.AccountId, x.Kind, x.PeriodEnd, x.Version }).IsUnique();
        statement.HasIndex(x => new { x.AccountId, x.SequenceNumber }).IsUnique();
        statement.HasIndex(x => new { x.AccountId, x.NotificationPostingId }).IsUnique().HasFilter("[NotificationPostingId] IS NOT NULL");
        statement.HasIndex(x => new { x.IssuerBankId, x.CreatedAt });
        statement.HasIndex(x => new { x.RecipientBankId, x.CreatedAt });
        var entry = model.Entity<AccountStatementEntry>();
        entry.HasOne(x => x.Statement).WithMany(x => x.Entries).HasForeignKey(x => x.StatementId).OnDelete(DeleteBehavior.Restrict);
        entry.HasOne<CorrespondentLedgerEntry>().WithMany().HasForeignKey(x => x.LedgerEntryId).OnDelete(DeleteBehavior.Restrict);
        entry.HasOne<WireTransfer>().WithMany().HasForeignKey(x => x.WireTransferId).OnDelete(DeleteBehavior.Restrict);
        entry.HasIndex(x => new { x.StatementId, x.LedgerEntryId }).IsUnique();
        var schedule = model.Entity<StatementSchedule>();
        schedule.Property(x => x.LeaseToken).IsConcurrencyToken();
        schedule.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        schedule.HasIndex(x => x.AccountId).IsUnique();
        schedule.HasIndex(x => new { x.Enabled, x.NextRunAt });
        var delivery = model.Entity<StatementDelivery>();
        delivery.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        delivery.HasOne(x => x.Statement).WithMany().HasForeignKey(x => x.StatementId).OnDelete(DeleteBehavior.Restrict);
        delivery.HasOne<Bank>().WithMany().HasForeignKey(x => x.RecipientBankId).OnDelete(DeleteBehavior.Restrict);
        delivery.HasIndex(x => new { x.StatementId, x.RecipientBankId }).IsUnique();
        var reconciliation = model.Entity<StatementReconciliation>();
        reconciliation.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        reconciliation.HasOne(x => x.Statement).WithMany().HasForeignKey(x => x.StatementId).OnDelete(DeleteBehavior.Restrict);
        reconciliation.HasOne<Bank>().WithMany().HasForeignKey(x => x.BankId).OnDelete(DeleteBehavior.Restrict);
        reconciliation.HasIndex(x => new { x.StatementId, x.CreatedAt });
        foreach (var type in new[] { typeof(CorrespondentAccount), typeof(CorrespondentLedgerEntry), typeof(AccountStatement), typeof(AccountStatementEntry), typeof(StatementReconciliation) })
            foreach (var property in model.Entity(type).Metadata.GetProperties().Where(x => x.ClrType == typeof(decimal)))
                property.SetPrecision(19);
        foreach (var type in new[] { typeof(CorrespondentAccount), typeof(CorrespondentLedgerEntry), typeof(AccountStatement), typeof(AccountStatementEntry), typeof(StatementReconciliation) })
            foreach (var property in model.Entity(type).Metadata.GetProperties().Where(x => x.ClrType == typeof(decimal)))
                property.SetScale(4);
    }
}
