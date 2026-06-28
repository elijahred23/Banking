using System.Globalization;
using System.Text;
using Banking.Domain;

namespace Banking.Infrastructure.Checks;

/// <summary>Writes a readable X9.37/X9.100-187-inspired teaching payload, not a certified file.</summary>
public sealed class X937CashLetterWriter
{
    public string Write(CheckCashLetter cashLetter, IReadOnlyList<CheckDeposit> deposits)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Record("01", "FILE_HEADER", cashLetter.DestinationRoutingNumber,
            cashLetter.OriginRoutingNumber, DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            cashLetter.FileIdModifier));
        sb.AppendLine(Record("10", "CASH_LETTER_HEADER", cashLetter.OriginRoutingNumber,
            deposits.Count.ToString(CultureInfo.InvariantCulture)));
        sb.AppendLine(Record("20", "BUNDLE_HEADER", cashLetter.OriginRoutingNumber, "1"));
        foreach (var deposit in deposits)
        {
            sb.AppendLine(Record("25", "CHECK_DETAIL", deposit.PayingRoutingNumber,
                deposit.PayingAccountNumber, deposit.CheckNumber, AmountCents(deposit.Amount),
                deposit.RawMicrLine));
            sb.AppendLine(Record("26", "CHECK_DETAIL_ADDENDUM_A",
                deposit.DepositoryBank.RoutingNumber, deposit.DepositorName));
            foreach (var image in deposit.Images.OrderBy(x => x.Side))
            {
                sb.AppendLine(Record("50", "IMAGE_VIEW_DETAIL", image.Side.ToString(),
                    image.Format.ToString(), image.SizeBytes.ToString(CultureInfo.InvariantCulture),
                    image.Sha256Hash));
                sb.AppendLine(Record("52", "IMAGE_VIEW_DATA", image.Side.ToString(),
                    Convert.ToBase64String(image.Content)));
            }
        }
        var total = AmountCents(deposits.Sum(x => x.Amount));
        var count = deposits.Count.ToString(CultureInfo.InvariantCulture);
        sb.AppendLine(Record("70", "BUNDLE_CONTROL", count, total));
        sb.AppendLine(Record("90", "CASH_LETTER_CONTROL", count, total));
        sb.AppendLine(Record("99", "FILE_CONTROL", count, total));
        return sb.ToString();
    }

    private static string AmountCents(decimal amount) =>
        decimal.ToInt64(decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero))
            .ToString("0000000000", CultureInfo.InvariantCulture);
    private static string Record(string type, params string[] fields) =>
        $"{type}|{string.Join('|', fields.Select(x => x.Replace('|', ' ')))}";
}
