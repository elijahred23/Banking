namespace Banking.Domain;

public sealed record AchValidationError(string Field, string Message);

public static class AchRules
{
    public static bool IsCredit(AchTransactionCode code) =>
        code is AchTransactionCode.CheckingCredit or AchTransactionCode.SavingsCredit;

    public static bool IsDebit(AchTransactionCode code) => !IsCredit(code);

    public static string ServiceClassCode(IEnumerable<AchEntry> entries)
    {
        var directions = entries.Select(x => IsCredit(x.TransactionCode)).Distinct().ToArray();
        return directions.Length != 1 ? "200" : directions[0] ? "220" : "225";
    }

    public static IReadOnlyList<AchValidationError> Validate(AchEntry entry, DateOnly processingDate)
    {
        var errors = new List<AchValidationError>();
        if (entry.Amount <= 0 || decimal.Round(entry.Amount, 2) != entry.Amount)
            errors.Add(new(nameof(entry.Amount), "ACH amounts must be positive and have no more than two decimal places."));
        if (entry.EffectiveEntryDate < processingDate)
            errors.Add(new(nameof(entry.EffectiveEntryDate), "The effective entry date cannot be in the past."));
        else if (!AchBusinessCalendar.IsBankingDay(entry.EffectiveEntryDate))
            errors.Add(new(nameof(entry.EffectiveEntryDate), "The effective entry date must be a Federal Reserve banking day."));
        if (!AbaRoutingNumberValidator.IsValid(entry.ReceivingRoutingNumber))
            errors.Add(new(nameof(entry.ReceivingRoutingNumber), "The receiving routing number is not a valid ABA routing number."));
        if (string.IsNullOrWhiteSpace(entry.ReceivingAccountNumber)
            || entry.ReceivingAccountNumber.Length > 17
            || entry.ReceivingAccountNumber.Any(char.IsControl))
            errors.Add(new(nameof(entry.ReceivingAccountNumber), "The receiving account number must contain 1 to 17 printable characters."));
        if (entry.SecCode == AchStandardEntryClass.Iat)
            errors.Add(new(nameof(entry.SecCode), "IAT entries are outside this simulator's supported ACH profile."));
        if (entry.SecCode is AchStandardEntryClass.Web or AchStandardEntryClass.Tel && IsCredit(entry.TransactionCode))
            errors.Add(new(nameof(entry.TransactionCode), $"{entry.SecCode.ToString().ToUpperInvariant()} supports consumer debit entries only."));
        if (entry.Purpose == AchPaymentPurpose.Payroll && (entry.SecCode != AchStandardEntryClass.Ppd || !IsCredit(entry.TransactionCode)))
            errors.Add(new(nameof(entry.Purpose), "Payroll must be submitted as a PPD credit."));
        if (entry.Purpose == AchPaymentPurpose.VendorPayment && entry.SecCode is not (AchStandardEntryClass.Ccd or AchStandardEntryClass.Ctx))
            errors.Add(new(nameof(entry.Purpose), "Vendor payments must use CCD or CTX."));
        if (entry.Purpose == AchPaymentPurpose.ConsumerDebit
            && (entry.SecCode is not (AchStandardEntryClass.Ppd or AchStandardEntryClass.Web or AchStandardEntryClass.Tel)
                || !IsDebit(entry.TransactionCode)))
            errors.Add(new(nameof(entry.Purpose), "Consumer debits must use a debit transaction code with PPD, WEB, or TEL."));
        if (entry.Purpose == AchPaymentPurpose.TaxPaymentEftps
            && (entry.SecCode != AchStandardEntryClass.Ccd || !IsCredit(entry.TransactionCode)
                || string.IsNullOrWhiteSpace(entry.Addenda05)))
            errors.Add(new(nameof(entry.Purpose), "EFTPS-style payments require a CCD credit and an addenda 05 record."));
        return errors;
    }
}

public static class AchBusinessCalendar
{
    public static bool IsBankingDay(DateOnly date) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !Holidays(date.Year).Contains(date);

    public static DateOnly NextBankingDay(DateOnly date)
    {
        do date = date.AddDays(1); while (!IsBankingDay(date));
        return date;
    }

    private static HashSet<DateOnly> Holidays(int year)
    {
        var holidays = new HashSet<DateOnly>
        {
            Observed(new(year, 1, 1)),
            NthWeekday(year, 1, DayOfWeek.Monday, 3),
            NthWeekday(year, 2, DayOfWeek.Monday, 3),
            LastWeekday(year, 5, DayOfWeek.Monday),
            Observed(new(year, 6, 19)),
            Observed(new(year, 7, 4)),
            NthWeekday(year, 9, DayOfWeek.Monday, 1),
            NthWeekday(year, 10, DayOfWeek.Monday, 2),
            Observed(new(year, 11, 11)),
            NthWeekday(year, 11, DayOfWeek.Thursday, 4),
            Observed(new(year, 12, 25))
        };
        var nextNewYearObserved = Observed(new(year + 1, 1, 1));
        if (nextNewYearObserved.Year == year) holidays.Add(nextNewYearObserved);
        return holidays;
    }

    private static DateOnly Observed(DateOnly date) => date.DayOfWeek switch
    {
        // Reserve Banks remain open on the Friday preceding a Saturday holiday.
        DayOfWeek.Saturday => date,
        DayOfWeek.Sunday => date.AddDays(1),
        _ => date
    };

    private static DateOnly NthWeekday(int year, int month, DayOfWeek day, int occurrence)
    {
        var date = new DateOnly(year, month, 1);
        while (date.DayOfWeek != day) date = date.AddDays(1);
        return date.AddDays(7 * (occurrence - 1));
    }

    private static DateOnly LastWeekday(int year, int month, DayOfWeek day)
    {
        var date = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        while (date.DayOfWeek != day) date = date.AddDays(-1);
        return date;
    }
}
