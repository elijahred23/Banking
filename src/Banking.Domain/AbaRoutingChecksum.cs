namespace Banking.Domain;

/// <summary>Shared ABA check digit calculation; rail-specific prefix rules are applied separately.</summary>
public static class AbaRoutingChecksum
{
    public static bool IsValid(string routingNumber)
    {
        if (routingNumber.Length != 9 || routingNumber.Any(c => !char.IsDigit(c))) return false;
        var digits = routingNumber.Select(c => c - '0').ToArray();
        var checksum = 3 * (digits[0] + digits[3] + digits[6])
            + 7 * (digits[1] + digits[4] + digits[7])
            + digits[2] + digits[5] + digits[8];
        return checksum % 10 == 0;
    }
}
