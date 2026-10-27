namespace Banking.Domain;

public enum AchTrafficMode { Payroll, Suppliers, Utilities, Mixed, Rent, Subscriptions, Insurance, Reimbursements }

public static class AchTraffic
{
    public const string Prefix = "ach-traffic:";
    public const string Maker = "ach-traffic:maker";
    public const string Approver = "ach-traffic:approver";
    public static string BankPrefix(Guid bankId) => $"{Prefix}{bankId:N}:";
    public static string StateName(Guid bankId, AchTrafficMode mode) => $"{BankPrefix(bankId)}{mode}";
    public static AchTrafficMode Mode(string name) => Enum.TryParse<AchTrafficMode>(name.Split(':')[^1], out var mode)
        && Enum.IsDefined(mode) ? mode : AchTrafficMode.Payroll;
    public static string Title(AchTrafficMode mode) => mode switch
    {
        AchTrafficMode.Payroll => "Payroll", AchTrafficMode.Suppliers => "Supplier payments",
        AchTrafficMode.Utilities => "Utility collections", AchTrafficMode.Rent => "Rent collection",
        AchTrafficMode.Subscriptions => "Subscriptions", AchTrafficMode.Insurance => "Insurance premiums",
        AchTrafficMode.Reimbursements => "Expense reimbursements", _ => "City mix"
    };
    public static bool IsDebitActivity(AchTrafficMode mode) => mode is AchTrafficMode.Utilities or AchTrafficMode.Rent or AchTrafficMode.Subscriptions or AchTrafficMode.Insurance;
    public static AchTrafficMode ActivityOf(AchEntry entry)
    {
        var words = (entry.Addenda05 ?? "").Split(' ');
        return words.Length > 1 && Enum.TryParse<AchTrafficMode>(words[1], true, out var mode) && Enum.IsDefined(mode)
            ? mode : entry.Purpose == AchPaymentPurpose.Payroll ? AchTrafficMode.Payroll : entry.Purpose == AchPaymentPurpose.ConsumerDebit ? AchTrafficMode.Utilities : AchTrafficMode.Suppliers;
    }
    public static bool IsTerminal(AchEntryStatus status) => status is AchEntryStatus.Posted
        or AchEntryStatus.Returned or AchEntryStatus.Rejected or AchEntryStatus.NocReceived;

    public static IReadOnlyList<AchEntry> CreateRun(Account company, IReadOnlyList<Account> receivers,
        AchTrafficMode mode, DateOnly processingDate, DateTimeOffset now, Random? random = null)
    {
        if (mode == AchTrafficMode.Mixed || !Enum.IsDefined(mode))
            throw new InvalidOperationException("Choose a concrete ACH activity for each run.");
        if (company.Status != AccountStatus.Open || receivers.Count == 0
            || receivers.Any(x => x.Status != AccountStatus.Open || x.Customer.BankId == company.Customer.BankId)
            || receivers.Select(x => x.Id).Distinct().Count() != receivers.Count)
            throw new InvalidOperationException("ACH traffic requires distinct open accounts at different banks.");
        var date = AchBusinessCalendar.IsBankingDay(processingDate)
            ? processingDate : AchBusinessCalendar.NextBankingDay(processingDate);
        var runId = Guid.NewGuid().ToString("N");
        var debit = IsDebitActivity(mode);
        var budget = Math.Max(0m, company.AvailableBalance / (receivers.Count * 4m));
        var entries = new List<AchEntry>();
        foreach (var receiver in receivers)
        {
            var amount = mode switch
            {
                AchTrafficMode.Payroll => TrafficAmounts.Sample(650m, 2400m, budget, random),
                AchTrafficMode.Suppliers => TrafficAmounts.Sample(125m, 1800m, budget, random),
                AchTrafficMode.Reimbursements => TrafficAmounts.Sample(25m, 450m, budget, random),
                AchTrafficMode.Rent => TrafficAmounts.Sample(650m, 1600m, Math.Max(0m, receiver.AvailableBalance / 4m), random),
                AchTrafficMode.Subscriptions => TrafficAmounts.Sample(9m, 79m, Math.Max(0m, receiver.AvailableBalance / 10m), random),
                AchTrafficMode.Insurance => TrafficAmounts.Sample(85m, 350m, Math.Max(0m, receiver.AvailableBalance / 10m), random),
                _ => TrafficAmounts.Sample(35m, 220m, Math.Max(0m, receiver.AvailableBalance / 10m), random)
            };
            if (amount == 0) continue;
            var entry = new AchEntry
            {
                OriginatingBankId = company.Customer.BankId, OriginatingAccountId = company.Id,
                ReceivingBankId = receiver.Customer.BankId, ReceivingRoutingNumber = receiver.Customer.Bank.RoutingNumber,
                ReceivingAccountNumber = receiver.AccountNumber, ReceiverName = Truncate(receiver.Customer.Name, 22),
                CompanyName = Truncate(company.Customer.Name, 16), CompanyId = "L" + company.Customer.Bank.RoutingNumber,
                SecCode = mode is AchTrafficMode.Payroll or AchTrafficMode.Reimbursements ? AchStandardEntryClass.Ppd
                    : debit ? AchStandardEntryClass.Web : AchStandardEntryClass.Ccd,
                TransactionCode = debit ? AchTransactionCode.CheckingDebit : AchTransactionCode.CheckingCredit,
                Purpose = mode is AchTrafficMode.Payroll or AchTrafficMode.Reimbursements ? AchPaymentPurpose.Payroll
                    : debit ? AchPaymentPurpose.ConsumerDebit : AchPaymentPurpose.VendorPayment,
                EntryDescription = mode switch { AchTrafficMode.Payroll => "PAYROLL", AchTrafficMode.Suppliers => "SUPPLIER",
                    AchTrafficMode.Rent => "RENT", AchTrafficMode.Subscriptions => "SUBSCRIPT", AchTrafficMode.Insurance => "INSURANCE",
                    AchTrafficMode.Reimbursements => "REIMBURSE", _ => "UTIL BILL" },
                Addenda05 = $"LAB {mode.ToString().ToUpperInvariant()} RUN {runId}",
                Amount = amount, EffectiveEntryDate = date, CreatedDate = now,
                Status = AchEntryStatus.PendingApproval, CreatedBy = Maker
            };
            var errors = AchRules.Validate(entry, processingDate);
            if (errors.Count != 0) throw new InvalidOperationException(string.Join(" ", errors.Select(x => x.Message)));
            entries.Add(entry);
        }
        return entries;
    }

    public static void Approve(AchEntry entry, DateTimeOffset now)
    {
        if (entry.CreatedBy != Maker || entry.Status != AchEntryStatus.PendingApproval)
            throw new InvalidOperationException("The ACH traffic approver only releases pending simulator entries.");
        entry.ApprovedBy = Approver;
        entry.ApprovedDate = now;
        entry.ApprovalDecisionReason = "Operations-enabled lab activity. Utility mandates are synthetic teaching authorizations.";
        entry.Status = AchEntryStatus.Entered;
    }

    private static string Truncate(string value, int length) => value[..Math.Min(value.Length, length)];
}
