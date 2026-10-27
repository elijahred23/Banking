using System.ComponentModel.DataAnnotations;

namespace Banking.Domain;

public enum StudioWireScenario { Normal, Delayed, Rejected, DuplicateDelivery, ComplianceReview, OutOfOrder }

public sealed class StudioSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    [MaxLength(8)] public string Kind { get; set; } = "Wire";
    public PaymentRail Rail { get; set; } = PaymentRail.Fedwire;
    public StudioWireScenario WireScenario { get; set; } = StudioWireScenario.Delayed;
    public AchProcessingScenario AchScenario { get; set; }
    public int? RemainingRuns { get; set; }
    [MaxLength(32)] public string Preset { get; set; } = "Custom";
    public bool UseRoster { get; set; }
    public DateTimeOffset UpdatedDate { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PayrollMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    public Guid EmployerAccountId { get; set; }
    public Account EmployerAccount { get; set; } = null!;
    public Guid CheckingAccountId { get; set; }
    public Account CheckingAccount { get; set; } = null!;
    public Guid? SavingsAccountId { get; set; }
    public Account? SavingsAccount { get; set; }
    public decimal NetPay { get; set; }
    public decimal SavingsPercent { get; set; } = 10m;
    public bool Enabled { get; set; } = true;
    public DateOnly NextPayDate { get; set; }
}

public sealed class BalanceReceipt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    // Wire correlation ID or ACH entry ID. Both sides resolve receipts through their own bank scope.
    public Guid PaymentKey { get; set; }
    public PaymentRail Rail { get; set; }
    [MaxLength(80)] public string AccountCode { get; set; } = "";
    [MaxLength(120)] public string AccountLabel { get; set; } = "";
    [MaxLength(16)] public string Movement { get; set; } = "Settlement";
    public decimal Before { get; set; }
    public decimal Delta { get; set; }
    public decimal After { get; set; }
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
}

public static class StudioPolicy
{
    public static ProcessingScenario Scenario(StudioWireScenario scenario) => scenario switch
    {
        StudioWireScenario.Delayed => ProcessingScenario.PendingThenAccepted,
        StudioWireScenario.Rejected => ProcessingScenario.FedRejects,
        StudioWireScenario.DuplicateDelivery => ProcessingScenario.DuplicateDelivery,
        StudioWireScenario.OutOfOrder => ProcessingScenario.OutOfOrderDelivery,
        _ => ProcessingScenario.Standard
    };
    public static bool WireRail(PaymentRail rail) => rail is PaymentRail.Fedwire or PaymentRail.FedNow or PaymentRail.SwiftCbprPlus;
    public static bool AchScenario(AchProcessingScenario scenario) => scenario is AchProcessingScenario.Standard
        or AchProcessingScenario.InsufficientFunds or AchProcessingScenario.AccountClosed or AchProcessingScenario.AccountNotFound
        or AchProcessingScenario.NocCorrectedAccount or AchProcessingScenario.NocCorrectedRouting
        or AchProcessingScenario.NocCorrectedAccountAndRouting or AchProcessingScenario.ControlTotalMismatch;
    public static IReadOnlyList<(string Id, string Title)> WirePresets { get; } =
        [("Custom", "Custom activity"), ("BusinessMorning", "Busy business morning · 10 wires"),
         ("RailComparison", "Compare three rails · 3 wires"), ("ExceptionDrill", "Rejection drill · 3 wires")];
    public static IReadOnlyList<(string Id, string Title)> AchPresets { get; } =
        [("Custom", "Custom activity"), ("FridayPayday", "Friday payday · 2 runs"),
         ("MonthEnd", "Month-end bills · 3 runs"), ("BusinessMorning", "Supplier morning · 3 runs")];
    public static DateOnly BankingDate(DateOnly date) => AchBusinessCalendar.IsBankingDay(date) ? date : AchBusinessCalendar.NextBankingDay(date);
    public static DateOnly Friday(DateOnly date)
    {
        while (date.DayOfWeek != DayOfWeek.Friday) date = date.AddDays(1);
        return BankingDate(date);
    }
    public static DateOnly MonthEnd(DateOnly date) => BankingDate(new DateOnly(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month)));

    public static IReadOnlyList<AchEntry> Payroll(PayrollMember member, DateOnly processingDate, DateTimeOffset now)
    {
        if (!member.Enabled || member.NetPay <= 0 || decimal.Round(member.NetPay, 2) != member.NetPay
            || member.SavingsPercent is < 0 or > 100 || member.EmployerAccount.Customer.BankId != member.BankId
            || member.CheckingAccount.Customer.BankId == member.BankId
            || member.CheckingAccount.Status != AccountStatus.Open || member.EmployerAccount.Status != AccountStatus.Open
            || member.SavingsPercent > 0 && (member.SavingsAccount is null || member.SavingsAccount.Status != AccountStatus.Open
                || member.SavingsAccount.CustomerId != member.CheckingAccount.CustomerId))
            throw new InvalidOperationException("Payroll requires a valid employer, employee, salary, and savings allocation.");
        var company = member.EmployerAccount;
        var receivers = new[] { member.CheckingAccount };
        // Build standard PPD metadata, then allocate the durable roster's salary exactly.
        var entry = AchTraffic.CreateRun(company, receivers, AchTrafficMode.Payroll, processingDate, now).SingleOrDefault();
        if (entry is null || !AccountAvailabilityPolicy.CanDebit(company, member.NetPay)) return [];
        var savings = decimal.Round(member.NetPay * member.SavingsPercent / 100m, 2, MidpointRounding.AwayFromZero);
        var checking = member.NetPay - savings;
        var result = new List<AchEntry>();
        if (checking > 0) { entry.Amount = checking; result.Add(entry); }
        if (savings > 0)
        {
            var save = AchTraffic.CreateRun(company, [member.SavingsAccount!], AchTrafficMode.Payroll, processingDate, now).Single();
            save.Amount = savings; save.TransactionCode = AchTransactionCode.SavingsCredit;
            save.Addenda05 = entry.Addenda05; result.Add(save);
        }
        foreach (var item in result) item.EffectiveEntryDate = BankingDate(member.NextPayDate > processingDate ? member.NextPayDate : processingDate);
        return result;
    }
}
