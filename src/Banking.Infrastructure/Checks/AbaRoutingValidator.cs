using Banking.Domain;

namespace Banking.Infrastructure.Checks;

public static class AbaRoutingValidator
{
    public static bool IsValid(string routingNumber) => AbaRoutingChecksum.IsValid(routingNumber);
}
