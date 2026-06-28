using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Banking.CheckImageExchangeSimulator;

public sealed class Worker(IMessageBus bus, IDbContextFactory<BankingDbContext> dbFactory,
    ILogger<Worker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        bus.ConsumeAsync<CheckCashLetterEnvelope>(Queues.CheckOutbound, ProcessAsync, stoppingToken);

    private async Task ProcessAsync(CheckCashLetterEnvelope envelope, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var cashLetter = await db.CheckCashLetters.Include(x => x.Deposits)
            .ThenInclude(x => x.DepositingAccount)
            .SingleOrDefaultAsync(x => x.Id == envelope.CashLetterId, token);
        if (cashLetter is null || cashLetter.Status == "Processed") return;

        foreach (var deposit in cashLetter.Deposits)
        {
            if (deposit.Status is CheckDepositStatus.Settled or CheckDepositStatus.Returned) continue;
            var payingBank = await db.Banks.SingleOrDefaultAsync(
                x => x.RoutingNumber == deposit.PayingRoutingNumber, token);
            deposit.PayingBankId = payingBank?.Id;
            deposit.Status = CheckDepositStatus.PresentedToPayingBank;
            deposit.Events.Add(Event("PresentedToPayingBank", payingBank is null
                ? "No paying bank found for the MICR routing number."
                : $"Presented to paying bank {payingBank.Name}."));

            if (payingBank is null)
            {
                Return(db, deposit, "U01", "Paying bank not found.");
                continue;
            }
            if (deposit.Scenario == CheckProcessingScenario.PayingBankReturns)
            {
                Return(db, deposit, "R01", "Simulated paying-bank return: insufficient funds.");
                continue;
            }
            var duplicate = deposit.Scenario == CheckProcessingScenario.DuplicatePresentment
                || await db.CheckDeposits.AnyAsync(x => x.Id != deposit.Id
                    && x.PayingRoutingNumber == deposit.PayingRoutingNumber
                    && x.PayingAccountNumber == deposit.PayingAccountNumber
                    && x.CheckNumber == deposit.CheckNumber && x.Amount == deposit.Amount
                    && x.Status == CheckDepositStatus.Settled, token);
            if (duplicate)
            {
                Return(db, deposit, "DUP", "Duplicate presentment detected.");
                continue;
            }
            var payingAccount = await db.Accounts.Include(x => x.Customer).SingleOrDefaultAsync(x =>
                x.Customer.BankId == payingBank.Id && x.AccountNumber == deposit.PayingAccountNumber,
                token);
            if (payingAccount is null)
            {
                Return(db, deposit, "R03", "Paying account not found.");
                continue;
            }
            if (payingAccount.AvailableBalance < deposit.Amount)
            {
                Return(db, deposit, "R01", "Insufficient funds in the paying account.");
                continue;
            }
            var depositoryBank = await db.Banks.SingleAsync(x => x.Id == deposit.DepositoryBankId, token);
            Settle(db, deposit, payingAccount, payingBank, depositoryBank);
        }
        cashLetter.Status = "Processed";
        await db.SaveChangesAsync(token);
        var settledIds = cashLetter.Deposits.Where(x => x.Status == CheckDepositStatus.Settled)
            .Select(x => x.Id).ToList();
        var returns = cashLetter.Deposits.Where(x => x.Status == CheckDepositStatus.Returned)
            .Select(x => new CheckReturnItem(x.Id, x.ReturnCode!, x.ReturnReason!)).ToList();
        await bus.PublishAsync(Queues.CheckInbound, new CheckOperatorResult(cashLetter.Id, true,
            "Check image exchange accepted and processed the cash letter.", settledIds), token);
        if (returns.Count != 0)
            await bus.PublishAsync(Queues.CheckReturnInbound,
                new CheckReturnFile(cashLetter.Id, returns), token);
        if (settledIds.Count != 0)
            await bus.PublishAsync(Queues.CheckSettled,
                new CheckSettlementNotice(cashLetter.Id, settledIds), token);
        logger.LogInformation("Processed check cash letter {CashLetterId}", cashLetter.Id);
    }

    private static void Settle(BankingDbContext db, CheckDeposit deposit, Account payingAccount,
        Bank payingBank, Bank depositoryBank)
    {
        payingAccount.Balance -= deposit.Amount;
        deposit.DepositingAccount.Balance += deposit.Amount;
        payingBank.MasterAccountBalance -= deposit.Amount;
        depositoryBank.MasterAccountBalance += deposit.Amount;
        deposit.Status = CheckDepositStatus.Settled;
        deposit.Events.Add(Event("Settled",
            "Paying account debited and depositor account credited through simulated check clearing."));
        var journal = Guid.NewGuid();
        db.CheckLedgerEntries.AddRange(
            Entry(journal, deposit.Id, $"CUSTOMER:{payingAccount.AccountNumber}",
                "Paying customer deposits", deposit.Amount, 0, "Debit maker deposit liability"),
            Entry(journal, deposit.Id, $"CHECK_CLEARING:{payingBank.Id:N}",
                "Paying-bank check clearing", 0, deposit.Amount, "Credit outgoing check clearing"),
            Entry(journal, deposit.Id, $"CHECK_CLEARING:{depositoryBank.Id:N}",
                "Depository-bank check clearing", deposit.Amount, 0, "Debit incoming check clearing"),
            Entry(journal, deposit.Id, $"CUSTOMER:{deposit.DepositingAccount.AccountNumber}",
                "Depositor customer deposits", 0, deposit.Amount, "Credit depositor deposit liability"));
    }

    private static void Return(BankingDbContext db, CheckDeposit deposit, string code, string reason)
    {
        deposit.Status = CheckDepositStatus.Returned;
        deposit.ReturnCode = code;
        deposit.ReturnReason = reason;
        deposit.Events.Add(Event("Returned", reason));
        db.CheckReturns.Add(new CheckReturn
            { CheckDepositId = deposit.Id, ReturnCode = code, Reason = reason });
    }

    private static CheckEvent Event(string type, string description) =>
        new() { EventType = type, Description = description };
    private static CheckLedgerEntry Entry(Guid journal, Guid depositId, string code,
        string name, decimal debit, decimal credit, string description) => new()
        { JournalId = journal, CheckDepositId = depositId, AccountCode = code,
            AccountName = name, Debit = debit, Credit = credit, Description = description };
}
