namespace Banking.Domain;

/// <summary>Opt-in teaching traffic; never used to authorize user-created payments.</summary>
public static class WireTraffic
{
    public const string Prefix = "wire-traffic:";
    public static bool TryGetBankId(string name, out Guid bankId)
    {
        bankId = default;
        return name.StartsWith(Prefix, StringComparison.Ordinal)
            && Guid.TryParse(name[Prefix.Length..], out bankId);
    }

    public const string Maker = "traffic-simulator:maker";
    public const string Approver = "traffic-simulator:approver";
    public static string StateName(Guid bankId) => $"{Prefix}{bankId:N}";
    public static bool IsGenerated(WireTransfer wire) => wire.CreatedBy == Maker;
    public static decimal Amount(Account source) => Math.Min(250m, decimal.Floor(source.AvailableBalance / 10m));

    public static WireTransfer Create(Account source, Account destination, DateTimeOffset now, Random? random = null, PaymentRail rail = PaymentRail.Fedwire,
        ProcessingScenario scenario = ProcessingScenario.PendingThenAccepted, bool complianceReview = false)
    {
        if (!StudioPolicy.WireRail(rail)) throw new InvalidOperationException("Choose a wire rail.");
        if (source.Customer.BankId == destination.Customer.BankId)
            throw new InvalidOperationException("Traffic must cross banks.");
        var amount = TrafficAmounts.Sample(1m, 250m, Amount(source), random);
        if (!AccountAvailabilityPolicy.CanDebit(source, amount) || amount < 1m
            || destination.Status != AccountStatus.Open)
            throw new InvalidOperationException("Traffic requires open accounts and available funds.");
        var wire = new WireTransfer
        {
            BankId = source.Customer.BankId, SenderBankId = source.Customer.BankId,
            ReceiverBankId = destination.Customer.BankId, FromAccountId = source.Id,
            BeneficiaryAccountNumber = destination.AccountNumber,
            SenderName = source.Customer.Name, ReceiverName = complianceReview ? "SANCTIONED PARTY (lab)" : destination.Customer.Name,
            Amount = amount, Direction = WireDirection.Outgoing, Rail = rail,
            Status = WireStatus.PendingApproval, CreatedBy = Maker, CreatedDate = now,
            Scenario = scenario
        };
        wire.Events.Add(new WireEvent { WireTransferId = wire.Id, EventType = "Created",
            Description = "Lab traffic maker generated a customer wire. No money has moved." });
        return wire;
    }

    public static void Approve(WireTransfer wire, DateTimeOffset now)
    {
        if (!IsGenerated(wire) || wire.Direction != WireDirection.Outgoing
            || wire.Status != WireStatus.PendingApproval)
            throw new InvalidOperationException("The traffic approver only releases pending simulator wires.");
        WireWorkflow.Transition(wire, WireStatus.Created);
        wire.ApprovedBy = Approver;
        wire.ApprovedDate = now;
        wire.Events.Add(new WireEvent { WireTransferId = wire.Id, EventType = "Approved",
            Description = "Released by the separate simulated approver under the Operations-enabled lab traffic policy. Human payment approvals are unchanged." });
    }
}
