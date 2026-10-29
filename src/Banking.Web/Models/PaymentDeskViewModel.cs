using Banking.Domain;
using Banking.Infrastructure;

namespace Banking.Web.Models;

public sealed record PaymentDeskItem(Guid Id, string Rail, string Party, decimal Amount, string Status,
    string Controller, PaymentAttention Attention, DateTimeOffset CreatedAt);
public sealed record PaymentDeskViewModel(Bank Bank, SimulationState Clock, LabClockMilestone Next,
    IReadOnlyList<PaymentDeskItem> Items, PaymentReconciliationResult Reconciliation,
    IReadOnlyList<DeskJournal> Journals, decimal WireDemand, decimal AchCreditExposure);
public sealed record DeskJournal(Guid PaymentId, string Controller, string Rail, Guid JournalId,
    decimal Debits, decimal Credits, int Lines)
{
    public bool Balanced => Debits == Credits && Lines >= 2;
}
