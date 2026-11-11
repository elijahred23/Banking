using Banking.Domain;
using Microsoft.EntityFrameworkCore;
namespace Banking.Infrastructure;
internal static class CorrespondentPlatformConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        foreach (var type in new[] { typeof(CorrespondentAuthorizationEvent), typeof(CorrespondentServiceAgreement), typeof(CorrespondentBookOrder), typeof(CorrespondentFundingRule), typeof(CorrespondentFundingRun) })
        {
            var entity = model.Entity(type);
            entity.Property("Id").ValueGeneratedNever();
            foreach (var property in entity.Metadata.GetProperties().Where(p => p.ClrType == typeof(decimal)))
            { property.SetPrecision(19); property.SetScale(4); }
        }
        model.Entity<CorrespondentAuthorizationEvent>().HasOne<CorrespondentAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<CorrespondentAuthorizationEvent>().HasOne<Bank>().WithMany().HasForeignKey(x => x.BankId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<CorrespondentAuthorizationEvent>().HasIndex(x => new { x.AccountId, x.CreatedAt });
        model.Entity<CorrespondentAuthorizationEvent>().HasIndex(x => x.BankId);
        model.Entity<CorrespondentServiceAgreement>().Ignore(x => x.IsActive);
        model.Entity<CorrespondentServiceAgreement>().HasIndex(x => x.AccountId).IsUnique();
        model.Entity<CorrespondentServiceAgreement>().HasOne<CorrespondentAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<CorrespondentBookOrder>().HasIndex(x => new { x.SourceAccountId, x.Status });
        model.Entity<CorrespondentBookOrder>().Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        model.Entity<CorrespondentFundingRule>().Property(x => x.Strategy).HasConversion<string>().HasMaxLength(20);
        model.Entity<CorrespondentFundingRule>().HasIndex(x => new { x.Enabled, x.NextRunAt });
        foreach (var type in new[] { typeof(CorrespondentBookOrder), typeof(CorrespondentFundingRule) })
            foreach (var name in new[] { "SourceAccountId", "DestinationAccountId" })
                model.Entity(type).HasOne(typeof(CorrespondentAccount)).WithMany().HasForeignKey(name).OnDelete(DeleteBehavior.Restrict);
        model.Entity<CorrespondentFundingRun>().HasOne<CorrespondentFundingRule>().WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<CorrespondentFundingRun>().HasIndex(x => new { x.RuleId, x.ScheduledFor }).IsUnique();
    }
}
