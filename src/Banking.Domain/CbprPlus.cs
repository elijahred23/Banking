using System.Xml.Linq;

namespace Banking.Domain;

public sealed record CbprPlusValidationResult(bool IsValid, IReadOnlyList<string> Errors);

/// <summary>
/// Publicly documented CBPR+ customer-payment rules represented by this learning lab.
/// MyStandards validation is still required for production conformance.
/// </summary>
public static class CbprPlusProfile
{
    public const string BusinessService = "swift.cbprplus.02";
    public const string MessageDefinitionId = "pacs.008.001.08";
    public const string SettlementMethod = "INDA";
    public const string ChargeBearer = "SHAR";

    public static bool IsValidBic(string? value) => value is { Length: 8 or 11 }
        && value[..6].All(IsUpperAsciiLetter)
        && value[6..].All(x => IsUpperAsciiLetter(x) || x is >= '0' and <= '9');

    private static bool IsUpperAsciiLetter(char value) => value is >= 'A' and <= 'Z';
}

public interface ICbprPlusMessageService
{
    CbprPlusValidationResult ValidateCustomerCreditTransfer(string xml);
}

public sealed class CbprPlusMessageService(IIsoMessageService iso) : ICbprPlusMessageService
{
    public CbprPlusValidationResult ValidateCustomerCreditTransfer(string xml)
    {
        var baseResult = iso.Validate(xml);
        var errors = baseResult.Errors.ToList();
        if (baseResult.MessageType != "pacs.008")
            errors.Add("CBPR+ customer payments must use pacs.008.");
        if (!baseResult.IsValid && baseResult.MessageType == "unknown")
            return new(false, errors.Distinct().ToList());

        var document = XDocument.Parse(xml);
        string? Value(string localName) => document.Descendants()
            .SingleOrDefault(x => x.Name.LocalName == localName)?.Value;
        var header = document.Descendants().SingleOrDefault(x => x.Name.LocalName == "AppHdr");
        if (header?.Descendants().SingleOrDefault(x => x.Name.LocalName == "MsgDefIdr")?.Value
            != CbprPlusProfile.MessageDefinitionId)
            errors.Add($"CBPR+ customer payments require {CbprPlusProfile.MessageDefinitionId}.");
        if (header?.Descendants().SingleOrDefault(x => x.Name.LocalName == "BizSvc")?.Value
            != CbprPlusProfile.BusinessService)
            errors.Add($"Business service must be {CbprPlusProfile.BusinessService}.");
        if (Value("SttlmMtd") != CbprPlusProfile.SettlementMethod)
            errors.Add("The lab's CBPR+ serial method requires settlement method INDA.");
        if (Value("ChrgBr") != CbprPlusProfile.ChargeBearer)
            errors.Add("The lab's CBPR+ customer-payment profile requires charge bearer SHAR.");
        var amount = document.Descendants().SingleOrDefault(x => x.Name.LocalName == "IntrBkSttlmAmt");
        if (amount?.Attribute("Ccy")?.Value != "USD")
            errors.Add("The lab's CBPR+ customer-payment profile currently supports USD only.");
        if (Value("NbOfTxs") != "1")
            errors.Add("A CBPR+ customer-payment message must contain one transaction in this lab.");
        if (document.Descendants().Count(x => x.Name.LocalName == "BICFI") != 4
            || document.Descendants().Where(x => x.Name.LocalName == "BICFI")
                .Any(x => !CbprPlusProfile.IsValidBic(x.Value)))
            errors.Add("Header and payment agents require valid BICFIs.");
        var addresses = document.Descendants().Where(x => x.Name.LocalName == "PstlAdr").ToList();
        if (addresses.Count != 2 || addresses.Any(x =>
                string.IsNullOrWhiteSpace(x.Elements().SingleOrDefault(y => y.Name.LocalName == "TwnNm")?.Value)
                || !IsCountryCode(x.Elements().SingleOrDefault(y => y.Name.LocalName == "Ctry")?.Value)))
            errors.Add("Debtor and creditor require structured town and country address data.");
        return new(errors.Count == 0, errors.Distinct().ToList());
    }

    private static bool IsCountryCode(string? value) => value is { Length: 2 }
        && value.All(x => x is >= 'A' and <= 'Z');
}
