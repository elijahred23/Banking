using Banking.Domain;

namespace Banking.Web.Models;

public sealed record CustomerCashFlow(Account Account, decimal PendingPayments, decimal PendingCollections);
public sealed record CampaignRoomViewModel(Bank Bank, string Kind, StudioCampaign? Selected,
    IReadOnlyList<StudioCampaign> Campaigns, IReadOnlyList<StudioScenario> Scenarios,
    IReadOnlyList<StudioDay> Days, IReadOnlyList<StudioNote> Notes, int PendingResponses,
    IReadOnlyList<CustomerCashFlow> CashFlows, IReadOnlyList<StudioDebitAuthorization> Authorizations,
    IReadOnlyList<AchEntry> RepairCandidates, IReadOnlyList<StudioAchRepair> Repairs,
    IReadOnlyList<WireTransfer> Wires, IReadOnlyList<WireTransfer> IncomingFundingWires,
    IReadOnlyList<StudioFundingRequest> Funding, DateOnly LabDate, int Outstanding, IReadOnlyList<CampaignDayEvidence> Assessments);
public sealed record CampaignDayEvidence(Guid DayId, int Generated, int Outstanding, int MissingJournals, int UnbalancedJournals, string Result);
public sealed record StudioReplayEvent(DateTimeOffset At, string Type, Guid PaymentId, string Text, decimal Debit = 0, decimal Credit = 0);
public sealed record StudioReplayViewModel(StudioDay Day, IReadOnlyList<StudioReplayEvent> Events);
