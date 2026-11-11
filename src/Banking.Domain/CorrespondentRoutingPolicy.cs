namespace Banking.Domain;

/// <summary>Deterministic, cycle-safe routing over configured, active correspondent relationships.</summary>
public static class CorrespondentRoutingPolicy
{
    public static ResolvedPaymentRoute Resolve(
        IEnumerable<CorrespondentRelationship> source, Guid originBankId, Guid destinationBankId,
        string currencyCode, string rail)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(currencyCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(rail);
        var currency = currencyCode.Trim().ToUpperInvariant();
        var normalizedRail = rail.Trim().ToUpperInvariant();
        var relationships = source.Where(x => x.IsActive && x.CurrencyCode == currency
                && x.Rail == normalizedRail)
            .OrderBy(x => x.Priority).ThenBy(x => x.Id).ToList();
        // Breadth-first search prefers fewer transport boundaries; relationship priority
        // and ID provide deterministic tie breaking. Each bank is visited once.
        var pending = new Queue<(Guid Bank, List<CorrespondentRelationship> Path)>();
        var visited = new HashSet<Guid> { originBankId };
        pending.Enqueue((originBankId, []));
        while (pending.TryDequeue(out var current))
        {
            foreach (var edge in relationships.Where(x => x.FromBankId == current.Bank))
            {
                if (!visited.Add(edge.ToBankId)) continue;
                var path = new List<CorrespondentRelationship>(current.Path) { edge };
                if (edge.ToBankId == destinationBankId)
                    return Route(originBankId, destinationBankId, currency, normalizedRail,
                        path.Select((leg, index) => new ResolvedPaymentRouteStep(index + 1,
                            leg.FromBankId, leg.ToBankId, path.Count == 1 ? "Direct"
                                : index == 0 ? "OriginToIntermediary"
                                : index == path.Count - 1 ? "IntermediaryToBeneficiary"
                                : "IntermediaryToIntermediary")).ToArray());
                pending.Enqueue((edge.ToBankId, path));
            }
        }

        throw new InvalidOperationException(
            $"No active {normalizedRail} correspondent route found from bank {originBankId} " +
            $"to bank {destinationBankId} for {currency}.");
    }

    private static ResolvedPaymentRoute Route(Guid origin, Guid destination, string currency,
        string rail, params ResolvedPaymentRouteStep[] steps) =>
        new(origin, destination, currency, rail, steps);
}
