using Banking.Domain;

namespace Banking.Web.Models;

public sealed record StudioInboxItem(Guid PaymentId, string Controller, string Title, string Status, string NextAction);
public sealed record StudioJournalEvidence(Guid JournalId, Guid PaymentId, decimal Debits, decimal Credits, int Lines)
{
    public bool Balanced => Lines >= 2 && Debits > 0 && Debits == Credits;
}
public sealed record StudioDayPanelViewModel(string Kind, StudioDay? Day, StudioSettings? Settings,
    Bank Bank, decimal CustomerBalance, decimal Holds, DateTimeOffset LabTime,
    IReadOnlyList<StudioInboxItem> Inbox, IReadOnlyList<OperationsCase> Cases,
    IReadOnlyList<StudioJournalEvidence> Journals, int Generated, int Settled, int Outstanding,
    decimal InFlightAmount, int AcceptedWithoutJournal, int RosterCount, Guid? InquiryPaymentId);
