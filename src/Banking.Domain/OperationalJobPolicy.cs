using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Banking.Domain;

public static class OperationalJobPolicy
{
    public static bool IsPending(WireStatus status) => status is not (WireStatus.Settled or WireStatus.Completed or WireStatus.Rejected or WireStatus.Returned);
    public static bool IsPending(AchEntryStatus status) => status is AchEntryStatus.PendingApproval or AchEntryStatus.Entered or AchEntryStatus.Validated or AchEntryStatus.Batched or AchEntryStatus.SentToOperator;
    public static bool IsStuck(WireTransfer wire, DateTimeOffset lastEventAt, DateTimeOffset now) =>
        IsPending(wire.Status) && wire.Status != WireStatus.PendingApproval && now - lastEventAt >= TimeSpan.FromMinutes(15);
    public static bool IsStuck(AchEntry entry, DateTimeOffset now, DateOnly labDate, DateTimeOffset? lastProgressAt = null) =>
        IsPending(entry.Status) && entry.Status != AchEntryStatus.PendingApproval
        && !(entry.Status == AchEntryStatus.Batched && entry.EffectiveEntryDate > labDate)
        && now - (lastProgressAt ?? entry.ApprovedDate ?? entry.CreatedDate) >= TimeSpan.FromHours(1);
    public static string Fingerprint(params string[] parts) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join('|', parts.Select(x => x.Trim().ToUpperInvariant())))))[..24];
    public static string DuplicateKey(WireTransfer wire) => Fingerprint(wire.Rail.ToString(), wire.TransferType.ToString(),
        wire.SenderBankId.ToString(), wire.ReceiverBankId.ToString(), wire.FromAccountId?.ToString() ?? "",
        wire.SenderName, wire.BeneficiaryAccountNumber, wire.Amount.ToString("F2", CultureInfo.InvariantCulture));
    public static string DuplicateKey(AchEntry entry) => Fingerprint(entry.OriginatingAccountId.ToString(), entry.CompanyId,
        entry.SecCode.ToString(), entry.TransactionCode.ToString(), entry.ReceivingRoutingNumber,
        entry.ReceivingAccountNumber, entry.Amount.ToString("F2", CultureInfo.InvariantCulture), entry.EffectiveEntryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}
