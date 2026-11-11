using Banking.Domain;
using Banking.Infrastructure;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class PaymentRouteResolverTests
{
    private readonly Guid _origin = Guid.NewGuid();
    private readonly Guid _intermediary = Guid.NewGuid();
    private readonly Guid _destination = Guid.NewGuid();

    [Fact]
    public void Resolve_prefers_direct_route()
    {
        var relationships = new[]
        {
            Edge(_origin, _intermediary, 1),
            Edge(_intermediary, _destination, 1),
            Edge(_origin, _destination, 100, "Direct")
        };

        var result = PaymentRouteResolver.ResolveFromRelationships(relationships,
            _origin, _destination, "usd", "swift");

        var step = Assert.Single(result.Steps);
        Assert.Equal("Direct", step.StepType);
        Assert.Equal(_destination, step.ToBankId);
    }

    [Fact]
    public void Resolve_selects_one_intermediary_in_priority_order()
    {
        var otherIntermediary = Guid.NewGuid();
        var relationships = new[]
        {
            Edge(_origin, otherIntermediary, 20),
            Edge(otherIntermediary, _destination, 1),
            Edge(_origin, _intermediary, 1),
            Edge(_intermediary, _destination, 1)
        };

        var result = PaymentRouteResolver.ResolveFromRelationships(relationships,
            _origin, _destination, "USD", "SWIFT");

        Assert.Collection(result.Steps,
            first =>
            {
                Assert.Equal("OriginToIntermediary", first.StepType);
                Assert.Equal(_intermediary, first.ToBankId);
            },
            second =>
            {
                Assert.Equal("IntermediaryToBeneficiary", second.StepType);
                Assert.Equal(_destination, second.ToBankId);
            });
    }

    [Fact]
    public void Resolve_rejects_when_only_route_is_inactive()
    {
        var relationships = new[] { Edge(_origin, _destination, 1, active: false) };

        var error = Assert.Throws<InvalidOperationException>(() =>
            PaymentRouteResolver.ResolveFromRelationships(relationships,
                _origin, _destination, "USD", "SWIFT"));

        Assert.Contains("No active SWIFT correspondent route", error.Message);
    }

    [Fact]
    public void Resolve_supports_multiple_intermediaries_and_ignores_cycles()
    {
        var second = Guid.NewGuid();
        var relationships = new[] {
            Edge(_origin, _origin, 0), Edge(_origin, _intermediary, 1),
            Edge(_intermediary, _origin, 0), Edge(_intermediary, second, 1),
            Edge(second, _destination, 1) };
        var route = PaymentRouteResolver.ResolveFromRelationships(relationships,
            _origin, _destination, "USD", "SWIFT");
        Assert.Equal(3, route.Steps.Count);
        Assert.Equal("IntermediaryToIntermediary", route.Steps[1].StepType);
        Assert.Equal(new[] { 1, 2, 3 }, route.Steps.Select(x => x.StepNumber));
        Assert.Equal(_destination, route.Steps[^1].ToBankId);
    }

    [Fact]
    public void Resolve_prefers_shorter_path_over_higher_priority_long_path()
    {
        var second = Guid.NewGuid();
        var route = PaymentRouteResolver.ResolveFromRelationships(new[] {
            Edge(_origin, _intermediary, 1), Edge(_intermediary, second, 1),
            Edge(second, _destination, 1), Edge(_origin, second, 100) },
            _origin, _destination, "USD", "SWIFT");
        Assert.Equal(2, route.Steps.Count);
        Assert.Equal(second, route.Steps[0].ToBankId);
    }

    private static CorrespondentRelationship Edge(Guid from, Guid to, int priority,
        string type = "Intermediary", bool active = true) => new()
    {
        FromBankId = from, ToBankId = to, CurrencyCode = "USD", Rail = "SWIFT",
        RelationshipType = type, Priority = priority, IsActive = active
    };
}
