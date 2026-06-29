using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public sealed class PaymentRouteResolver(IDbContextFactory<BankingDbContext> dbFactory)
    : IPaymentRouteResolver
{
    public async Task<ResolvedPaymentRoute> ResolveRouteAsync(Guid originBankId,
        Guid destinationBankId, string currencyCode, string rail,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currencyCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(rail);
        var currency = currencyCode.Trim().ToUpperInvariant();
        var normalizedRail = rail.Trim().ToUpperInvariant();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var relationships = await db.CorrespondentRelationships.AsNoTracking()
            .Where(x => x.CurrencyCode == currency && x.Rail == normalizedRail && x.IsActive)
            .OrderBy(x => x.Priority).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        return ResolveFromRelationships(relationships, originBankId, destinationBankId,
            currency, normalizedRail);
    }

    public static ResolvedPaymentRoute ResolveFromRelationships(
        IEnumerable<CorrespondentRelationship> source, Guid originBankId, Guid destinationBankId,
        string currencyCode, string rail)
    {
        var currency = currencyCode.Trim().ToUpperInvariant();
        var normalizedRail = rail.Trim().ToUpperInvariant();
        var relationships = source.Where(x => x.IsActive && x.CurrencyCode == currency
                && x.Rail == normalizedRail)
            .OrderBy(x => x.Priority).ThenBy(x => x.Id).ToList();
        var direct = relationships.FirstOrDefault(x =>
            x.FromBankId == originBankId && x.ToBankId == destinationBankId);
        if (direct is not null)
            return Route(originBankId, destinationBankId, currency, normalizedRail,
                new ResolvedPaymentRouteStep(1, originBankId, destinationBankId, "Direct"));

        foreach (var firstLeg in relationships.Where(x => x.FromBankId == originBankId
                     && x.ToBankId != originBankId && x.ToBankId != destinationBankId))
        {
            var secondLeg = relationships.FirstOrDefault(x => x.FromBankId == firstLeg.ToBankId
                && x.ToBankId == destinationBankId);
            if (secondLeg is null) continue;
            return Route(originBankId, destinationBankId, currency, normalizedRail,
                new ResolvedPaymentRouteStep(1, originBankId, firstLeg.ToBankId, "OriginToIntermediary"),
                new ResolvedPaymentRouteStep(2, firstLeg.ToBankId, destinationBankId, "IntermediaryToBeneficiary"));
        }

        throw new InvalidOperationException(
            $"No active {normalizedRail} correspondent route found from bank {originBankId} " +
            $"to bank {destinationBankId} for {currency}.");
    }

    private static ResolvedPaymentRoute Route(Guid origin, Guid destination, string currency,
        string rail, params ResolvedPaymentRouteStep[] steps) =>
        new(origin, destination, currency, rail, steps);
}
