using System.Globalization;

namespace Banking.Domain;

public enum AchLearningCaseType { Standard, BlindRdfiException, BlindNotificationOfChange, BlindFileFailure }

public sealed record AchLearningDecision(string Action, string Layer, bool MovesValue, string Explanation);

public static class AchLearningPolicy
{
    public static AchProcessingScenario SelectScenario(AchLearningCaseType type, Guid seed)
    {
        var choices = type switch
        {
            AchLearningCaseType.BlindRdfiException => new[] { AchProcessingScenario.AccountNotFound,
                AchProcessingScenario.InvalidAccountNumber, AchProcessingScenario.InsufficientFunds,
                AchProcessingScenario.AccountClosed },
            AchLearningCaseType.BlindNotificationOfChange => new[] { AchProcessingScenario.NocCorrectedAccount,
                AchProcessingScenario.NocCorrectedRouting, AchProcessingScenario.NocCorrectedAccountAndRouting },
            AchLearningCaseType.BlindFileFailure => new[] { AchProcessingScenario.MalformedFile,
                AchProcessingScenario.ControlTotalMismatch },
            _ => new[] { AchProcessingScenario.Standard }
        };
        return choices[(int)((uint)seed.GetHashCode() % (uint)choices.Length)];
    }

    public static AchLearningDecision DecisionFor(AchEntry entry)
    {
        if (entry.Status == AchEntryStatus.PendingApproval)
            return new("Approve or reject under dual control", "Bank authorization", false,
                "Approval authorizes ODFI processing; it does not deliver or settle the entry.");
        if (entry.Status == AchEntryStatus.Rejected && !string.IsNullOrWhiteSpace(entry.ApprovalDecisionReason))
            return new("Retain the approver decision and do not transmit", "Bank authorization", false,
                "The approver rejected the instruction before ODFI validation, batching, or settlement.");
        if (entry.Status == AchEntryStatus.Rejected && entry.AchBatchId is null)
            return new("Repair or reject the invalid entry and release any applicable hold", "ODFI validation", false,
                "The ODFI rejected the individual entry before it entered an operator file.");
        if (entry.Scenario is AchProcessingScenario.MalformedFile or AchProcessingScenario.ControlTotalMismatch)
            return new("Reject the file and release applicable holds", "Operator file validation", false,
                "A file-level control or format failure prevents business acceptance and settlement.");
        if (entry.Scenario is AchProcessingScenario.NocCorrectedAccount or AchProcessingScenario.NocCorrectedRouting
            or AchProcessingScenario.NocCorrectedAccountAndRouting)
            return new("Record corrected information for future entries", "RDFI notification of change", false,
                "A notification of change communicates corrected data; it does not reverse this payment.");
        if (entry.Status == AchEntryStatus.Returned || entry.Scenario is AchProcessingScenario.InvalidRoutingNumber
            or AchProcessingScenario.AccountNotFound or AchProcessingScenario.InvalidAccountNumber
            or AchProcessingScenario.InsufficientFunds or AchProcessingScenario.AccountClosed)
            return new("Process an RDFI return and reconcile the reversal", "RDFI return", true,
                "A return is distinct from file rejection and sends value back through a reversing workflow.");
        return new("Reconcile settlement and the balanced journal", "Settlement/accounting", true,
            "The accepted entry should settle once and produce balanced, correlated journal lines.");
    }
}

public sealed record NachaFieldAnnotation(string Name, int Start, int Length, string RawValue, string Meaning);
public sealed record NachaRecordAnnotation(int Number, char Type, string Name, string Raw,
    IReadOnlyList<NachaFieldAnnotation> Fields);

public static class NachaRecordAnnotator
{
    public static IReadOnlyList<NachaRecordAnnotation> Annotate(string? payload)
    {
        var lines = (payload ?? "").Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        return lines.Select((line, index) => AnnotateLine(index + 1, line)).ToList();
    }

    private static NachaRecordAnnotation AnnotateLine(int number, string line)
    {
        var type = line.Length == 0 ? '?' : line[0];
        var (name, specs) = type switch
        {
            '1' => ("File header", new[] { S("Record type", 1, 1, "1 identifies a file header"),
                S("Priority code", 2, 2, "Operator processing priority"), S("Immediate destination", 4, 10, "ACH operator routing destination"),
                S("Immediate origin", 14, 10, "ODFI routing origin"), S("Creation date", 24, 6, "YYMMDD"),
                S("Creation time", 30, 4, "HHMM"), S("File ID modifier", 34, 1, "Distinguishes files created on the same date"),
                S("Record size", 35, 3, "94 characters"), S("Blocking factor", 38, 2, "10 records per block"),
                S("Format code", 40, 1, "NACHA format code"), S("Destination name", 41, 23, "Operator name"),
                S("Origin name", 64, 23, "ODFI name"), S("Reference code", 87, 8, "Origin reference") }),
            '5' => ("Batch header", new[] { S("Record type", 1, 1, "5 identifies a batch header"),
                S("Service class", 2, 3, "200 mixed, 220 credits, 225 debits"), S("Company name", 5, 16, "Originator name"),
                S("Company ID", 41, 10, "Originator identifier"), S("SEC code", 51, 3, "Standard Entry Class"),
                S("Entry description", 54, 10, "Originator description"), S("Effective date", 70, 6, "YYMMDD"),
                S("ODFI ID", 80, 8, "First eight digits of ODFI routing number"), S("Batch number", 88, 7, "Sequential batch number") }),
            '6' => ("Entry detail", new[] { S("Record type", 1, 1, "6 identifies an entry detail"),
                S("Transaction code", 2, 2, "Account type and credit/debit direction"), S("RDFI ID", 4, 8, "First eight routing digits"),
                S("Check digit", 12, 1, "Routing number check digit"), S("DFI account", 13, 17, "Receiving account number"),
                S("Amount", 30, 10, "Amount in cents"), S("Individual ID", 40, 15, "Receiver identification"),
                S("Receiver name", 55, 22, "Receiver name"), S("Addenda indicator", 79, 1, "1 when an addenda record follows"),
                S("Trace number", 80, 15, "ODFI ID plus sequence") }),
            '7' => ("Addenda", new[] { S("Record type", 1, 1, "7 identifies addenda"), S("Addenda type", 2, 2, "05 is payment-related information"),
                S("Payment information", 4, 80, "Additional remittance information"), S("Sequence", 84, 4, "Addenda sequence"),
                S("Entry sequence", 88, 7, "Links to entry trace sequence") }),
            '8' => ("Batch control", new[] { S("Record type", 1, 1, "8 identifies batch control"),
                S("Service class", 2, 3, "Must match the batch header"), S("Entry/addenda count", 5, 6, "Detail and addenda records"),
                S("Routing hash", 11, 10, "Truncated sum of RDFI identifiers"), S("Debit total", 21, 12, "Batch debits in cents"),
                S("Credit total", 33, 12, "Batch credits in cents"), S("Company ID", 45, 10, "Must match the batch header"),
                S("ODFI ID", 80, 8, "Originating DFI identification"), S("Batch number", 88, 7, "Must match the batch header") }),
            '9' when line.Any(c => c != '9') => ("File control", new[] { S("Record type", 1, 1, "9 identifies file control"),
                S("Batch count", 2, 6, "Number of batches"), S("Block count", 8, 6, "Number of ten-record blocks"),
                S("Entry/addenda count", 14, 8, "Detail and addenda records"), S("Routing hash", 22, 10, "Truncated sum of RDFI identifiers"),
                S("Debit total", 32, 12, "File debits in cents"), S("Credit total", 44, 12, "File credits in cents") }),
            '9' => ("Block padding", new[] { S("Padding", 1, 94, "9-filled record used to complete a ten-record block") }),
            _ => ("Unknown record", new[] { S("Record", 1, line.Length, "Unsupported or malformed record") })
        };
        var fields = specs.Select(x => Field(line, x)).ToList();
        return new(number, type, name, line, fields);
    }

    private sealed record Spec(string Name, int Start, int Length, string Meaning);
    private static Spec S(string name, int start, int length, string meaning) => new(name, start, length, meaning);
    private static NachaFieldAnnotation Field(string line, Spec spec)
    {
        var start = spec.Start - 1;
        var raw = start >= line.Length ? "" : line.Substring(start, Math.Min(spec.Length, line.Length - start));
        var meaning = spec.Name.Contains("total", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var cents)
            ? $"{spec.Meaning}: {cents / 100m:C}" : spec.Meaning;
        return new(spec.Name, spec.Start, spec.Length, raw, meaning);
    }
}
