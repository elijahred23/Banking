using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

internal static class FedFundsModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<FedFundsTrade>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<FedFundsQuote>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<FedFundsCreditLine>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<FedFundsApproval>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<FedFundsAccrual>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<FedFundsPaymentObligation>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<FedFundsEvent>().Property(x => x.Id).ValueGeneratedNever();
        var trade = model.Entity<FedFundsTrade>();
        trade.ToTable("FedFundsTrades", table => {
            table.HasCheckConstraint("CK_FedFundsTrades_Terms", "LenderBankId <> BorrowerBankId AND Principal > 0 AND AnnualRate >= 0 AND AnnualRate <= 100 AND MaturityDate > ValueDate AND (ProposalBankId = LenderBankId OR ProposalBankId = BorrowerBankId)"); });
        trade.Property(x => x.Principal).HasPrecision(19, 4);
        trade.Property(x => x.AnnualRate).HasPrecision(12, 8);
        trade.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        trade.Property(x => x.RowVersion).IsRowVersion();
        trade.HasOne<Bank>().WithMany().HasForeignKey(x => x.LenderBankId).OnDelete(DeleteBehavior.Restrict);
        trade.HasOne<Bank>().WithMany().HasForeignKey(x => x.BorrowerBankId).OnDelete(DeleteBehavior.Restrict);
        trade.HasOne<Bank>().WithMany().HasForeignKey(x => x.ProposalBankId).OnDelete(DeleteBehavior.Restrict);
        trade.HasIndex(x => new { x.LenderBankId, x.Status });
        trade.HasIndex(x => new { x.BorrowerBankId, x.Status });
        trade.HasIndex(x => new { x.Status, x.MaturityDate });
        var quote = model.Entity<FedFundsQuote>();
        quote.ToTable("FedFundsQuotes", t => t.HasCheckConstraint("CK_FedFundsQuotes_Terms", "MinAmount > 0 AND MaxAmount >= MinAmount AND Rate >= 0 AND Rate <= 100"));
        quote.Property(x => x.MinAmount).HasPrecision(19, 4);
        quote.Property(x => x.MaxAmount).HasPrecision(19, 4);
        quote.Property(x => x.Rate).HasPrecision(12, 8);
        quote.Property(x => x.Side).HasConversion<string>().HasMaxLength(10);
        quote.HasOne<Bank>().WithMany().HasForeignKey(x => x.BankId).OnDelete(DeleteBehavior.Restrict);
        quote.HasIndex(x => new { x.BankId, x.ExpiresAt });
        var line = model.Entity<FedFundsCreditLine>();
        line.ToTable("FedFundsCreditLines", t => t.HasCheckConstraint("CK_FedFundsCreditLines_Terms", "LenderBankId <> BorrowerBankId AND ApprovedLimit >= 0 AND ReservedAmount >= 0 AND ReservedAmount <= ApprovedLimit"));
        line.Property(x => x.ApprovedLimit).HasPrecision(19, 4);
        line.Property(x => x.ReservedAmount).HasPrecision(19, 4);
        line.Property(x => x.RowVersion).IsRowVersion();
        line.HasOne<Bank>().WithMany().HasForeignKey(x => x.LenderBankId).OnDelete(DeleteBehavior.Restrict);
        line.HasOne<Bank>().WithMany().HasForeignKey(x => x.BorrowerBankId).OnDelete(DeleteBehavior.Restrict);
        line.HasIndex(x => new { x.LenderBankId, x.BorrowerBankId }).IsUnique();
        var approval = model.Entity<FedFundsApproval>();
        approval.HasOne(x => x.Trade).WithMany(x => x.Approvals).HasForeignKey(x => x.TradeId).OnDelete(DeleteBehavior.Restrict);
        approval.HasOne<Bank>().WithMany().HasForeignKey(x => x.BankId).OnDelete(DeleteBehavior.Restrict);
        approval.HasIndex(x => new { x.TradeId, x.BankId }).IsUnique();
        var accrual = model.Entity<FedFundsAccrual>();
        accrual.Property(x => x.Amount).HasPrecision(19, 4);
        accrual.HasOne(x => x.Trade).WithMany(x => x.Accruals).HasForeignKey(x => x.TradeId).OnDelete(DeleteBehavior.Restrict);
        accrual.HasIndex(x => new { x.TradeId, x.AccrualDate }).IsUnique();
        var obligation = model.Entity<FedFundsPaymentObligation>();
        obligation.Property(x => x.Amount).HasPrecision(19, 4);
        obligation.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        obligation.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        obligation.HasOne(x => x.Trade).WithMany(x => x.Obligations).HasForeignKey(x => x.TradeId).OnDelete(DeleteBehavior.Restrict);
        obligation.HasOne<WireTransfer>().WithMany().HasForeignKey(x => x.WireTransferId).OnDelete(DeleteBehavior.Restrict);
        obligation.HasIndex(x => x.CorrelationId).IsUnique();
        obligation.HasIndex(x => new { x.TradeId, x.Type }).IsUnique();
        obligation.HasIndex(x => new { x.Status, x.DueDate });
        var ev = model.Entity<FedFundsEvent>();
        ev.HasOne(x => x.Trade).WithMany(x => x.Events).HasForeignKey(x => x.TradeId).OnDelete(DeleteBehavior.Restrict);
        ev.HasIndex(x => new { x.TradeId, x.CreatedAt });
    }
}
