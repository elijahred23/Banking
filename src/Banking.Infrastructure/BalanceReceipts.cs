using Banking.Domain;

namespace Banking.Infrastructure;

public static class BalanceReceipts
{
    public static void Customer(BankingDbContext db, Guid bankId, Guid paymentKey, PaymentRail rail,
        Account account, decimal delta, string movement = "Settlement") =>
        Add(db, bankId, paymentKey, rail, $"CUSTOMER:{account.Id:N}",
            $"Customer •••• {account.AccountNumber[^Math.Min(4, account.AccountNumber.Length)..]}", account.Balance, delta, movement);
    public static void Bank(BankingDbContext db, Guid paymentKey, PaymentRail rail, Bank bank, decimal delta,
        string movement = "Settlement") => Add(db, bank.Id, paymentKey, rail, $"BANK:{bank.Id:N}",
            "Bank settlement cash", bank.MasterAccountBalance, delta, movement);
    private static void Add(BankingDbContext db, Guid bankId, Guid key, PaymentRail rail, string code,
        string label, decimal after, decimal delta, string movement) => db.BalanceReceipts.Add(new BalanceReceipt
        { BankId = bankId, PaymentKey = key, Rail = rail, AccountCode = code, AccountLabel = label,
            Before = after - delta, Delta = delta, After = after, Movement = movement });
}
