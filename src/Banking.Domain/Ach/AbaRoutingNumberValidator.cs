namespace Banking.Domain;

public static class AbaRoutingNumberValidator
{
    public static bool IsValid(string? routingNumber)
    {
        if (routingNumber is null || routingNumber.Length != 9 || routingNumber.Any(c => !char.IsDigit(c)))
            return false;
        var digits = routingNumber.Select(c => c - '0').ToArray();
        var checksum = 3 * (digits[0] + digits[3] + digits[6])
            + 7 * (digits[1] + digits[4] + digits[7])
            + digits[2] + digits[5] + digits[8];
        return checksum % 10 == 0;
    }
}
