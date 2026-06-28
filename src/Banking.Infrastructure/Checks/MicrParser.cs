using System.Text.RegularExpressions;

namespace Banking.Infrastructure.Checks;

public sealed record MicrParseResult(string RoutingNumber, string AccountNumber,
    string CheckNumber, string NormalizedLine);

public static partial class MicrParser
{
    [GeneratedRegex(@"^t(?<routing>\d{9})t\s+(?<account>[0-9A-Za-z\-]{4,34})o\s+(?<check>\d{1,15})$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SimplePattern();

    public static MicrParseResult Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new ArgumentException("MICR line is required.");
        var normalized = raw.Trim().Replace("⑆", "t").Replace("⑈", "o");
        var match = SimplePattern().Match(normalized);
        if (!match.Success)
            throw new ArgumentException("MICR line must look like: t101000019t 123456789o 0001234");
        var routing = match.Groups["routing"].Value;
        if (!AbaRoutingValidator.IsValid(routing))
            throw new ArgumentException("MICR routing number failed ABA check digit validation.");
        return new(routing, match.Groups["account"].Value, match.Groups["check"].Value,
            normalized);
    }
}
