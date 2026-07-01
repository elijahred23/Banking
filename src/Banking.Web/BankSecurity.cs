using System.Security.Claims;

namespace Banking.Web;

internal static class BankSecurity
{
    public const string BankIdClaim = "bank_id";
    public const string PaymentCreator = "PaymentCreator";
    public const string PaymentApprover = "PaymentApprover";
    public const string ComplianceOfficer = "ComplianceOfficer";
    public const string Operations = "Operations";

    public static Guid BankId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(BankIdClaim), out var bankId)
            ? bankId : throw new InvalidOperationException("The signed-in user has no bank assignment.");
}
