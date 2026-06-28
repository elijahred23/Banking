using System.Globalization;

namespace Banking.Domain;

public static class EftpsTaxPaymentAddendaBuilder
{
    public static string Build(EftpsTaxPayment payment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payment.TaxpayerIdentificationNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(payment.TaxTypeCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(payment.TaxPeriodEndDate);
        if (payment.Amount <= 0) throw new ArgumentOutOfRangeException(nameof(payment.Amount));
        var cents = decimal.Round(payment.Amount * 100, 0, MidpointRounding.AwayFromZero)
            .ToString("0", CultureInfo.InvariantCulture);
        var value = $"TXP*{payment.TaxpayerIdentificationNumber}*{payment.TaxTypeCode}*{payment.TaxPeriodEndDate}*T*{cents}\\";
        if (value.Length > 80) throw new ArgumentException("TXP addenda exceeds the 80-character ACH addenda field.");
        return value;
    }
}
