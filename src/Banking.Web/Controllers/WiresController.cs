using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Globalization;
using System.Xml.Linq;

namespace Banking.Web.Controllers;

[Authorize]
public sealed class WiresController(IDbContextFactory<BankingDbContext> dbFactory,
    IIsoMessageService iso, ICbprPlusMessageService cbpr,
    IWireIsoMessageService wireMessages, INonValueMessageWorkflowService nonValueMessages) : Controller
{
    public async Task<IActionResult> Index(WireDirection? direction, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var query = db.WireTransfers.Where(x => x.BankId == bankId);
        if (direction is not null) query = query.Where(x => x.Direction == direction);
        var wires = await query.OrderByDescending(x => x.CreatedDate).AsNoTracking().ToListAsync(token);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);
        return View(new WireIndexViewModel(bank, direction, wires));
    }

    [HttpGet]
    public async Task<IActionResult> Instructions(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);

        var sourceAccounts = await db.Accounts
            .Where(x => x.Customer.BankId == bankId)
            .OrderBy(x => x.Customer.Name)
            .Select(x => new WireInstructionAccountViewModel(
                x.Customer.Name, x.AccountNumber, x.Balance - x.HeldBalance))
            .AsNoTracking()
            .ToListAsync(token);

        var destinations = await db.Accounts
            .Where(x => x.Customer.BankId != bankId)
            .OrderBy(x => x.Customer.Bank.Name)
            .ThenBy(x => x.Customer.Name)
            .Select(x => new
            {
                BankName = x.Customer.Bank.Name,
                CustomerName = x.Customer.Name,
                x.AccountNumber,
                x.Customer.Bank.RoutingNumber,
                x.Customer.Bank.Bic,
                x.Customer.Bank.CountryCode,
                x.Customer.Bank.FedNowEnabled,
                x.Customer.Bank.FedNowReceiveEnabled,
                x.Customer.Bank.FedNowOnline,
                x.Customer.Bank.SwiftEnabled
            })
            .AsNoTracking()
            .ToListAsync(token);

        var domestic = destinations.Where(x => x.CountryCode == bank.CountryCode)
            .Select(x => new WireInstructionDestinationViewModel(x.BankName, x.CustomerName, x.AccountNumber,
                x.RoutingNumber, x.Bic, x.CountryCode)).ToList();
        var fedNow = destinations.Where(x => x.CountryCode == bank.CountryCode
                && x.FedNowEnabled && x.FedNowReceiveEnabled && x.FedNowOnline)
            .Select(x => new WireInstructionDestinationViewModel(x.BankName, x.CustomerName, x.AccountNumber,
                x.RoutingNumber, x.Bic, x.CountryCode)).ToList();
        var international = destinations.Where(x => x.CountryCode != bank.CountryCode && x.SwiftEnabled)
            .Select(x => new WireInstructionDestinationViewModel(x.BankName, x.CustomerName, x.AccountNumber,
                x.RoutingNumber, x.Bic, x.CountryCode)).ToList();

        return View(new WireInstructionsViewModel(bank, sourceAccounts, domestic, fedNow, international));
    }

    [HttpGet]
    public async Task<IActionResult> MessageWorkflows(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        return View(await LoadMessageWorkflowsAsync(bankId, db, token));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRequestForPayment(RequestForPaymentViewModel model,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var account = await db.Accounts.Include(x => x.Customer).ThenInclude(x => x.Bank)
            .SingleOrDefaultAsync(x => x.Id == model.CreditorAccountId
                && x.Customer.BankId == bankId, token);
        var debtorBank = await db.Banks.SingleOrDefaultAsync(x => x.Id == model.DebtorBankId
            && x.Id != bankId, token);
        if (account is null) ModelState.AddModelError(nameof(model.CreditorAccountId),
            "Select an account owned by the active bank.");
        if (debtorBank is null) ModelState.AddModelError(nameof(model.DebtorBankId),
            "Select a different debtor bank.");
        if (model.Rail is not (PaymentRail.Fedwire or PaymentRail.FedNow))
            ModelState.AddModelError(nameof(model.Rail), "Select Fedwire or FedNow.");
        if (model.Rail == PaymentRail.FedNow
            && model.Amount > FedNowProfile.CustomerCreditTransferLimit)
            ModelState.AddModelError(nameof(model.Amount),
                $"FedNow payment requests cannot exceed {FedNowProfile.CustomerCreditTransferLimit:C0}.");
        if (model.Rail == PaymentRail.FedNow && debtorBank is not null && account is not null
            && (!account.Customer.Bank.FedNowRequestForPaymentEnabled
                || !debtorBank.FedNowReceiveEnabled || !debtorBank.FedNowOnline))
            ModelState.AddModelError(nameof(model.DebtorBankId),
                "Both institutions must be enabled and online for this FedNow request.");
        if (!ModelState.IsValid)
        {
            TempData["Error"] = string.Join(" ", ModelState.Values.SelectMany(x => x.Errors)
                .Select(x => x.ErrorMessage));
            return RedirectToAction(nameof(MessageWorkflows));
        }

        var exchange = new MessageExchange
        {
            BankId = bankId, CounterpartyBankId = debtorBank!.Id,
            Type = MessageExchangeType.RequestForPayment,
            Status = MessageExchangeStatus.Submitted,
            Rail = model.Rail, Subject = $"Request {model.Amount:C} from {model.DebtorName}",
            Details = model.Remittance.Trim(), AccountNumber = account!.AccountNumber,
            Amount = model.Amount
        };
        AddExchangeMessages(exchange, nonValueMessages.CreateRequestForPayment(exchange.Id,
            account.Customer.Bank, debtorBank, account.Customer.Name, account.AccountNumber,
            model.DebtorName.Trim(), model.DebtorAccount.Trim(), model.Amount,
            model.Remittance.Trim(), model.Rail));
        db.MessageExchanges.Add(exchange);
        await db.SaveChangesAsync(token);
        TempData["Notice"] =
            "Drawdown request delivered and acknowledged. It is waiting for the debtor bank to approve or reject it.";
        return RedirectToAction(nameof(MessageWorkflows));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentApprover)]
    public async Task<IActionResult> RespondToRequest(Guid id, bool approve, string? reason,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var exchange = await db.MessageExchanges.Include(x => x.Bank)
            .Include(x => x.CounterpartyBank).Include(x => x.Messages)
            .SingleOrDefaultAsync(x => x.Id == id
                && x.Type == MessageExchangeType.RequestForPayment
                && x.CounterpartyBankId == bankId, token);
        if (exchange is null) return NotFound();
        if (exchange.Status != MessageExchangeStatus.Submitted)
        {
            TempData["Error"] = "This request has already been answered.";
            return RedirectToAction(nameof(MessageWorkflows));
        }

        var debtorBank = exchange.CounterpartyBank!;
        WireTransfer? wire = null;
        if (approve)
        {
            var request = exchange.Messages.Single(x => x.MessageType == "pain.013");
            var debtorAccountNumber = ReadPaymentAccount(request.XmlPayload, "DbtrAcct");
            var creditorAccountNumber = ReadPaymentAccount(request.XmlPayload, "CdtrAcct");
            var debtorAccount = await db.Accounts.Include(x => x.Customer)
                .SingleOrDefaultAsync(x => x.Customer.BankId == bankId
                    && x.AccountNumber == debtorAccountNumber, token);
            if (debtorAccount is null)
            {
                TempData["Error"] =
                    "The requested debtor account does not exist at this bank, so the request cannot be approved.";
                return RedirectToAction(nameof(MessageWorkflows));
            }
            if (exchange.Amount is null || debtorAccount.AvailableBalance < exchange.Amount.Value)
            {
                TempData["Error"] = "The debtor account has insufficient available funds.";
                return RedirectToAction(nameof(MessageWorkflows));
            }
            if (exchange.Rail == PaymentRail.FedNow
                && (exchange.Amount.Value > FedNowProfile.CustomerCreditTransferLimit
                    || !debtorBank.FedNowSendEnabled || !debtorBank.FedNowOnline
                    || !exchange.Bank.FedNowReceiveEnabled || !exchange.Bank.FedNowOnline))
            {
                TempData["Error"] =
                    "The approved payment does not meet the FedNow amount or participant availability rules.";
                return RedirectToAction(nameof(MessageWorkflows));
            }

            var creditorAccount = await db.Accounts.Include(x => x.Customer)
                .SingleOrDefaultAsync(x => x.Customer.BankId == exchange.BankId
                    && x.AccountNumber == creditorAccountNumber, token);
            wire = new WireTransfer
            {
                BankId = bankId, SenderBankId = bankId, ReceiverBankId = exchange.BankId,
                FromAccountId = debtorAccount.Id, ToAccountId = creditorAccount?.Id,
                Direction = WireDirection.Outgoing, Status = WireStatus.Created,
                Amount = exchange.Amount!.Value, SenderName = debtorAccount.Customer.Name,
                ReceiverName = creditorAccount?.Customer.Name
                    ?? ReadPaymentParty(request.XmlPayload, "Cdtr"),
                BeneficiaryAccountNumber = creditorAccountNumber,
                Scenario = ProcessingScenario.Standard, Rail = exchange.Rail,
                TransferType = WireTransferType.CustomerCreditTransfer,
                CustomerReference = $"RFP-{exchange.Id:N}"[..35],
                CreatedBy = $"pain.013:{exchange.Id:N}", ApprovedBy = User.Identity!.Name,
                ApprovedDate = DateTimeOffset.UtcNow,
                Events = [new WireEvent { EventType = "Created",
                    Description = $"Payment created after approval of pain.013 request {exchange.Id.ToString()[..8]}." }]
            };
            db.WireTransfers.Add(wire);
            exchange.Status = MessageExchangeStatus.Responded;
        }
        else
        {
            exchange.Status = MessageExchangeStatus.Rejected;
        }

        var response = nonValueMessages.CreateRequestForPaymentResponse(exchange.Id,
            exchange.Bank, debtorBank, exchange.Rail, approve, reason);
        exchange.Messages.Add(new IsoMessage
        {
            MessageExchangeId = exchange.Id, MessageType = response.MessageType,
            Direction = response.Direction, XmlPayload = response.XmlPayload
        });
        exchange.UpdatedDate = DateTimeOffset.UtcNow;
        if (wire is not null)
            db.OutboxMessages.Add(Outbox.Message(Queues.WireCreated, new WireCreated(wire.Id)));
        try
        {
            await db.SaveChangesAsync(token);
        }
        catch (DbUpdateConcurrencyException)
        {
            TempData["Error"] = "This request was answered by another session.";
            return RedirectToAction(nameof(MessageWorkflows));
        }

        if (wire is not null)
        {
            TempData["Notice"] =
                $"Request approved. Payment {wire.Id.ToString()[..8]} was durably queued for processing.";
            return RedirectToAction(nameof(Details), new { id = wire.Id });
        }

        TempData["Notice"] = "Request rejected. A pain.014 rejection was returned to the creditor bank.";
        return RedirectToAction(nameof(MessageWorkflows));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateAccountReport(AccountReportRequestViewModel model,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var bank = await db.Banks.SingleAsync(x => x.Id == bankId, token);
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (model.BusinessDate != today || model.Rail is not (PaymentRail.Fedwire or PaymentRail.FedNow)
            || model.ReportType is not ("Account balance" or "Activity totals" or "Activity details"))
        {
            TempData["Error"] = "Select a supported report and today's business date.";
            return RedirectToAction(nameof(MessageWorkflows));
        }
        var exchange = new MessageExchange
        {
            BankId = bankId, Type = MessageExchangeType.AccountReport,
            Status = MessageExchangeStatus.Responded, Rail = model.Rail,
            Subject = model.ReportType, Details = $"{model.ReportType} for {model.BusinessDate:yyyy-MM-dd}",
            AccountNumber = bank.FedParticipantId
        };
        AddExchangeMessages(exchange, nonValueMessages.CreateAccountReport(exchange.Id, bank,
            model.ReportType, model.BusinessDate, bank.MasterAccountBalance, model.Rail));
        db.MessageExchanges.Add(exchange);
        await db.SaveChangesAsync(token);
        TempData["Notice"] = "Account report requested with camt.060 and returned with camt.052.";
        return RedirectToAction(nameof(MessageWorkflows));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateSystemEvent(SystemEventViewModel model,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var sender = await db.Banks.SingleAsync(x => x.Id == bankId, token);
        var recipient = await db.Banks.SingleOrDefaultAsync(x => x.Id == model.RecipientBankId
            && x.Id != bankId && x.FedNowEnabled, token);
        if (!ModelState.IsValid || recipient is null)
        {
            TempData["Error"] = "Select a FedNow participant and provide an event code and details.";
            return RedirectToAction(nameof(MessageWorkflows));
        }
        var exchange = new MessageExchange
        {
            BankId = bankId, CounterpartyBankId = recipient.Id,
            Type = MessageExchangeType.SystemEvent, Status = MessageExchangeStatus.Responded,
            Rail = PaymentRail.FedNow, Subject = model.EventCode.Trim(), Details = model.Details.Trim()
        };
        AddExchangeMessages(exchange, nonValueMessages.CreateSystemEvent(exchange.Id, sender,
            recipient, model.EventCode.Trim(), model.Details.Trim()));
        db.MessageExchanges.Add(exchange);
        await db.SaveChangesAsync(token);
        TempData["Notice"] = "Participant broadcast sent with admi.004 and acknowledged with admi.011.";
        return RedirectToAction(nameof(MessageWorkflows));
    }

    [HttpGet, Authorize(Roles = BankSecurity.PaymentCreator)]
    public async Task<IActionResult> Create(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        return View(await PopulateAsync(new CreateWireViewModel(), bankId, db, token));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentCreator)]
    public async Task<IActionResult> Create(CreateWireViewModel model, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var isInstitutionTransfer = model.TransferType == WireTransferType.FinancialInstitutionCreditTransfer;
        var account = isInstitutionTransfer || model.FromAccountId is null ? null
            : await db.Accounts.Include(x => x.Customer)
                .SingleOrDefaultAsync(x => x.Id == model.FromAccountId && x.Customer.BankId == bankId, token);
        var senderBank = await db.Banks.SingleAsync(x => x.Id == bankId, token);
        var receiverBank = await db.Banks.SingleOrDefaultAsync(x => x.Id == model.ReceiverBankId, token);
        if (!isInstitutionTransfer && account is null)
            ModelState.AddModelError(nameof(model.FromAccountId), "Select an account owned by the active bank.");
        if (!isInstitutionTransfer && string.IsNullOrWhiteSpace(model.ReceiverName))
            ModelState.AddModelError(nameof(model.ReceiverName), "Enter the beneficiary name.");
        if (!isInstitutionTransfer && string.IsNullOrWhiteSpace(model.BeneficiaryAccountNumber))
            ModelState.AddModelError(nameof(model.BeneficiaryAccountNumber), "Enter the beneficiary account or IBAN.");
        if (receiverBank is null || receiverBank.Id == bankId)
            ModelState.AddModelError(nameof(model.ReceiverBankId), "Select a different receiving bank.");
        if (model.Rail is not (PaymentRail.Fedwire or PaymentRail.FedNow or PaymentRail.SwiftCbprPlus))
            ModelState.AddModelError(nameof(model.Rail),
                "Select Fedwire, FedNow, or SWIFT CBPR+ for a wire payment.");
        if (!isInstitutionTransfer && model.Rail == PaymentRail.FedNow
            && model.Amount > FedNowProfile.CustomerCreditTransferLimit)
            ModelState.AddModelError(nameof(model.Amount),
                $"FedNow customer credit transfers cannot exceed {FedNowProfile.CustomerCreditTransferLimit:C0}.");
        if (isInstitutionTransfer && model.Rail == PaymentRail.SwiftCbprPlus)
            ModelState.AddModelError(nameof(model.Rail),
                "The lab supports pacs.009 financial institution transfers over Fedwire and FedNow only.");
        if (model.Rail == PaymentRail.FedNow && receiverBank is not null
            && (!receiverBank.FedNowEnabled || !receiverBank.FedNowReceiveEnabled || !receiverBank.FedNowOnline))
            ModelState.AddModelError(nameof(model.ReceiverBankId),
                "The receiving bank is not currently enabled and online to receive FedNow payments.");
        if (model.Rail == PaymentRail.SwiftCbprPlus && receiverBank is not null
            && !receiverBank.SwiftEnabled)
            ModelState.AddModelError(nameof(model.ReceiverBankId),
                "The receiving institution is not enabled for the SWIFT CBPR+ lab.");
        if (model.Rail == PaymentRail.SwiftCbprPlus && !senderBank.SwiftEnabled)
            ModelState.AddModelError(nameof(model.Rail),
                "The sending institution is not enabled for the SWIFT CBPR+ lab.");
        if (receiverBank is not null && receiverBank.CountryCode != senderBank.CountryCode
            && model.Rail != PaymentRail.SwiftCbprPlus)
            ModelState.AddModelError(nameof(model.Rail),
                "Cross-border payments must use the SWIFT international wire rail.");
        if (!isInstitutionTransfer && receiverBank is not null && receiverBank.CountryCode != "US"
            && model.Rail == PaymentRail.SwiftCbprPlus
            && !CbprPlusProfile.IsValidIban(model.BeneficiaryAccountNumber ?? ""))
            ModelState.AddModelError(nameof(model.BeneficiaryAccountNumber),
                "Enter a valid IBAN for this international beneficiary.");
        if (!ModelState.IsValid) return View(await PopulateAsync(model, bankId, db, token));

        var customerReference = model.CustomerReference.Trim().ToUpperInvariant();
        if (await db.WireTransfers.AnyAsync(x => x.BankId == bankId
            && x.CustomerReference == customerReference, token))
        {
            ModelState.AddModelError(nameof(model.CustomerReference),
                "This bank has already used that customer payment reference.");
            return View(await PopulateAsync(model, bankId, db, token));
        }

        var beneficiaryAccount = isInstitutionTransfer
            ? receiverBank!.FedParticipantId
            : model.Rail == PaymentRail.SwiftCbprPlus
            && CbprPlusProfile.IsValidIban(model.BeneficiaryAccountNumber ?? "")
                ? new string(model.BeneficiaryAccountNumber!.Where(char.IsLetterOrDigit).ToArray())
                    .ToUpperInvariant()
                : model.BeneficiaryAccountNumber!.Trim();
        var receiverAccount = isInstitutionTransfer ? null
            : await db.Accounts.Include(x => x.Customer).FirstOrDefaultAsync(x =>
                x.Customer.BankId == model.ReceiverBankId && x.AccountNumber == beneficiaryAccount, token);
        var wire = new WireTransfer
        {
            BankId = bankId, SenderBankId = bankId, ReceiverBankId = model.ReceiverBankId,
            FromAccountId = account?.Id, ToAccountId = receiverAccount?.Id,
            Direction = WireDirection.Outgoing, Status = WireStatus.PendingApproval, Amount = model.Amount,
            SenderName = isInstitutionTransfer ? senderBank.Name : account!.Customer.Name,
            ReceiverName = isInstitutionTransfer ? receiverBank!.Name : model.ReceiverName!.Trim(),
            BeneficiaryAccountNumber = beneficiaryAccount, Scenario = model.Scenario,
            Rail = model.Rail, TransferType = model.TransferType,
            CustomerReference = customerReference, CreatedBy = User.Identity!.Name,
            Events = [new WireEvent { EventType = "Created",
                Description = isInstitutionTransfer
                    ? $"Outgoing pacs.009 financial institution credit transfer created over {model.Rail} using the {model.Scenario} lab scenario."
                    : receiverBank!.CountryCode == senderBank.CountryCode
                    ? $"Outgoing {model.Rail} payment created using the {model.Scenario} lab scenario."
                    : $"Outgoing international USD wire to {receiverBank.CountryCode} created over SWIFT CBPR+ using the {model.Scenario} lab scenario." }]
        };
        db.WireTransfers.Add(wire);
        await db.SaveChangesAsync(token);
        TempData["Notice"] =
            $"Wire {wire.Id.ToString()[..8]} created. A different payment approver must release it.";
        return RedirectToAction(nameof(Details), new { id = wire.Id });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentApprover)]
    public async Task<IActionResult> DecideApproval(Guid id, bool approve, string? reason,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var wire = await db.WireTransfers.SingleOrDefaultAsync(x => x.Id == id
            && x.BankId == bankId && x.Direction == WireDirection.Outgoing, token);
        if (wire is null) return NotFound();
        if (wire.Status != WireStatus.PendingApproval)
        {
            TempData["Error"] = "This payment is no longer waiting for approval.";
            return RedirectToAction(nameof(Details), new { id });
        }
        if (string.Equals(wire.CreatedBy, User.Identity!.Name, StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "The payment creator cannot approve their own payment.";
            return RedirectToAction(nameof(Details), new { id });
        }

        wire.ApprovedBy = User.Identity.Name;
        wire.ApprovedDate = DateTimeOffset.UtcNow;
        wire.Status = approve ? WireStatus.Created : WireStatus.Rejected;
        if (approve)
            db.OutboxMessages.Add(Outbox.Message(Queues.WireCreated, new WireCreated(wire.Id)));
        db.WireEvents.Add(new WireEvent
        {
            WireTransferId = wire.Id,
            EventType = approve ? "Approved" : "ApprovalRejected",
            Description = approve
                ? $"Payment released by {User.Identity.Name} under dual control."
                : $"Payment rejected by {User.Identity.Name}: {NormalizeDecisionReason(reason)}"
        });
        try
        {
            await db.SaveChangesAsync(token);
        }
        catch (DbUpdateConcurrencyException)
        {
            TempData["Error"] = "Another approver already answered this payment.";
            return RedirectToAction(nameof(Details), new { id });
        }

        if (approve)
        {
            TempData["Notice"] = "Payment approved and durably queued for validation.";
        }
        else TempData["Notice"] = "Payment rejected before transmission; no funds moved.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.ComplianceOfficer)]
    public async Task<IActionResult> DecideCompliance(Guid id, bool clear, string? reason,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var wire = await db.WireTransfers.SingleOrDefaultAsync(x => x.Id == id
            && x.BankId == bankId && x.Status == WireStatus.PendingComplianceReview, token);
        if (wire is null) return NotFound();

        wire.ComplianceStatus = clear ? "ClearedByOfficer" : "Rejected";
        wire.ComplianceReason = clear ? NormalizeDecisionReason(reason)
            : NormalizeDecisionReason(reason);
        wire.ComplianceReviewedBy = User.Identity!.Name;
        wire.ComplianceReviewedDate = DateTimeOffset.UtcNow;
        wire.Status = clear ? WireStatus.Created : WireStatus.Rejected;
        db.WireEvents.Add(new WireEvent
        {
            WireTransferId = wire.Id,
            EventType = clear ? "ComplianceCleared" : "ComplianceRejected",
            Description = $"Compliance officer {User.Identity.Name} "
                + (clear ? "cleared the review hit" : "rejected the payment")
                + $": {wire.ComplianceReason}"
        });
        if (clear)
            db.OutboxMessages.Add(Outbox.Message(Queues.WireCreated, new WireCreated(wire.Id)));
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException)
        {
            TempData["Error"] = "Another compliance officer already answered this review.";
            return RedirectToAction(nameof(Details), new { id });
        }
        TempData["Notice"] = clear
            ? "Compliance review cleared; the payment was durably re-queued."
            : "Compliance review rejected; no funds moved.";
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Details(Guid id, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var model = await LoadDetailsAsync(id, bankId, db, token);
        return model is null ? NotFound() : View(model);
    }

    [HttpGet]
    public async Task<IActionResult> DetailsFragment(Guid id, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var model = await LoadDetailsAsync(id, bankId, db, token);
        return model is null ? NotFound() : PartialView("_WireDetails", model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenCase(Guid id, WireCaseType type, string reason,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var normalizedReason = reason?.Trim() ?? string.Empty;
        if (!Enum.IsDefined(type) || normalizedReason.Length is < 10 or > 500)
        {
            TempData["Error"] = "Provide a case reason between 10 and 500 characters.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var caseId = Guid.NewGuid();
        var strategy = db.Database.CreateExecutionStrategy();
        var outcome = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, token);
            var wire = await db.WireTransfers.Include(x => x.Cases)
                .SingleOrDefaultAsync(x => x.Id == id && x.BankId == bankId, token);
            if (wire is null) return OpenCaseOutcome.Missing();
            var existing = wire.Cases.SingleOrDefault(x => x.Id == caseId);
            if (existing is not null)
                return OpenCaseOutcome.Succeeded(
                    $"{CaseLabel(type)} {caseId.ToString()[..8]} processed with status {existing.Status}.");
            var eligible = type == WireCaseType.ReturnRequest
                ? WireCasePolicy.CanRequestReturn(wire)
                : WireCasePolicy.CanInvestigate(wire);
            if (!eligible)
                return OpenCaseOutcome.Failed(type == WireCaseType.ReturnRequest
                    ? "Only a settled outgoing payment can have a return requested."
                    : "This outgoing payment has not reached a stage that can be investigated.");
            if (wire.Cases.Any(x => x.Type == type && x.Status == WireCaseStatus.Submitted))
                return OpenCaseOutcome.Failed(
                    $"An open {CaseLabel(type).ToLowerInvariant()} already exists for this wire.");

            var banks = await db.Banks
                .Where(x => x.Id == wire.SenderBankId || x.Id == wire.ReceiverBankId)
                .ToDictionaryAsync(x => x.Id, token);
            var sender = banks[wire.SenderBankId];
            var receiver = banks[wire.ReceiverBankId];
            var wireCase = new WireCase
            {
                Id = caseId,
                WireTransferId = wire.Id,
                RequestedByBankId = bankId,
                Type = type,
                Reason = normalizedReason,
                RequestMessageType = WireCasePolicy.RequestMessageType(type, wire.Rail)
            };
            db.WireCases.Add(wireCase);
            db.IsoMessages.Add(new IsoMessage
            {
                WireTransferId = wire.Id,
                MessageType = wireCase.RequestMessageType,
                Direction = MessageDirection.Outbound,
                XmlPayload = CreateCaseRequest(wire, wireCase, sender, receiver)
            });
            db.WireEvents.Add(CaseEvent(wire.Id, "CaseSubmitted",
                $"{CaseLabel(type)} {wireCase.Id.ToString()[..8]} submitted using {wireCase.RequestMessageType}: {normalizedReason}"));

            if (type == WireCaseType.Investigation)
                ResolveInvestigation(db, wire, wireCase, sender, receiver);
            else
                await ResolveReturnRequestAsync(db, wire, wireCase, sender, receiver, token);

            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return OpenCaseOutcome.Succeeded(
                $"{CaseLabel(type)} {wireCase.Id.ToString()[..8]} processed with status {wireCase.Status}.");
        });

        if (outcome.NotFound) return NotFound();
        if (outcome.Error is not null) TempData["Error"] = outcome.Error;
        else TempData["Notice"] = outcome.Notice;
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateIsoMessage(Guid id, string messageType, string? details,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var wire = await db.WireTransfers.SingleOrDefaultAsync(
            x => x.Id == id && x.BankId == bankId, token);
        if (wire is null) return NotFound();
        if (details?.Length > 500)
        {
            TempData["Error"] = "Message details cannot exceed 500 characters.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var banks = await db.Banks.Where(x => x.Id == wire.SenderBankId || x.Id == wire.ReceiverBankId)
            .ToDictionaryAsync(x => x.Id, token);
        var debtorAccount = wire.FromAccountId is Guid accountId
            ? await db.Accounts.Where(x => x.Id == accountId).Select(x => x.AccountNumber)
                .SingleOrDefaultAsync(token)
            : null;
        try
        {
            var created = wireMessages.Create(messageType, wire, banks[wire.SenderBankId],
                banks[wire.ReceiverBankId], debtorAccount, details);
            db.IsoMessages.Add(new IsoMessage
            {
                WireTransferId = wire.Id,
                MessageType = created.MessageType,
                Direction = created.Direction,
                XmlPayload = created.XmlPayload
            });
            db.WireEvents.Add(CaseEvent(wire.Id, "MessageGenerated",
                $"{created.MessageType} {created.Direction.ToString().ToLowerInvariant()} message generated, validated, and persisted."));
            await db.SaveChangesAsync(token);
            TempData["Notice"] = $"{created.MessageType} generated and added to this wire.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<WireDetailsViewModel?> LoadDetailsAsync(Guid id, Guid bankId,
        BankingDbContext db, CancellationToken token)
    {
        var wire = await db.WireTransfers.Include(x => x.Bank).Include(x => x.Events)
            .Include(x => x.Cases)
            .Include(x => x.IsoMessages).AsSplitQuery().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.BankId == bankId, token);
        if (wire is null) return null;

        var deliveries = await db.MessageDeliveries.Where(x => x.WireTransferId == id)
            .OrderBy(x => x.UpdatedDate).AsNoTracking().ToListAsync(token);
        var route = await db.PaymentRoutes.Include(x => x.Steps).ThenInclude(x => x.FromBank)
            .Include(x => x.Steps).ThenInclude(x => x.ToBank).AsSplitQuery().AsNoTracking()
            .SingleOrDefaultAsync(x => x.PaymentId == id, token);
        var events = wire.Events.OrderBy(x => x.CreatedDate).ToList();
        var stages = events.Select((item, index) => new ProcessingStageViewModel(
            item,
            ServiceFor(item.EventType),
            index == 0 ? null : item.CreatedDate - events[index - 1].CreatedDate)).ToList();
        var messages = wire.IsoMessages.OrderBy(x => x.CreatedDate)
            .Select(x => BuildMessage(x, wire.Rail)).ToList();
        var ledgerEntries = await db.LedgerEntries.Where(x => x.WireTransferId == id)
            .OrderBy(x => x.CreatedDate).ThenBy(x => x.AccountCode).AsNoTracking().ToListAsync(token);
        var failure = deliveries.LastOrDefault(x => !string.IsNullOrWhiteSpace(x.LastError))?.LastError
            ?? events.LastOrDefault(x => x.EventType.Contains("Reject", StringComparison.OrdinalIgnoreCase))?.Description;
        var lastActivity = new[]
        {
            events.LastOrDefault()?.CreatedDate ?? wire.CreatedDate,
            wire.IsoMessages.OrderBy(x => x.CreatedDate).LastOrDefault()?.CreatedDate ?? wire.CreatedDate,
            deliveries.LastOrDefault()?.UpdatedDate ?? wire.CreatedDate
        }.Max();
        return new WireDetailsViewModel(wire, route, deliveries, stages, messages, ledgerEntries,
            wire.Cases.OrderByDescending(x => x.CreatedDate).ToList(),
            WireCasePolicy.CanRequestReturn(wire), WireCasePolicy.CanInvestigate(wire), failure,
            lastActivity - wire.CreatedDate, wireMessages.SupportedMessages);
    }

    private IsoMessageViewModel BuildMessage(IsoMessage message, PaymentRail rail)
    {
        try
        {
            var document = XDocument.Parse(message.XmlPayload);
            var result = iso.Validate(message.XmlPayload);
            var cbprResult = rail == PaymentRail.SwiftCbprPlus && result.MessageType == "pacs.008"
                ? cbpr.ValidateCustomerCreditTransfer(message.XmlPayload)
                : null;
            var valid = result.IsValid && cbprResult is not { IsValid: false };
            var validation = valid
                ? $"Valid {result.MessageType} lab profile · business header, UETR, and required fields present"
                : string.Join(" ", result.Errors.Concat(cbprResult?.Errors ?? []));
            return new IsoMessageViewModel(message, document.ToString(), true, valid, validation);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            return new IsoMessageViewModel(message, message.XmlPayload, false, false,
                $"Invalid XML · {ex.Message}");
        }
    }

    private static string ServiceFor(string eventType) => eventType switch
    {
        "Created" => "Banking.Web",
        "Validated" or "Rejected" or "IsoGenerated" or PaymentEventTypes.RouteSelected => "Banking.WireService",
        PaymentEventTypes.RouteStepStarted or PaymentEventTypes.RouteStepAccepted
            or PaymentEventTypes.RouteStepRejected or PaymentEventTypes.IntermediaryForwarded
            or PaymentEventTypes.BeneficiaryBankReceived => "Banking.MessageManager",
        "PendingAtFed" or "AcceptedByFed" or "RejectedByFed" => "Payment-network simulator → MessageManager",
        "SentToFed" or "Settled" or "Delivered" or "ReceivedFromFed" or "Posted" or "HoldReleased" or "Completed"
            => "Banking.MessageManager",
        "CaseSubmitted" or "CaseResolved" or "ReturnCompleted" or "ReturnRejected"
            or "MessageGenerated" => "Wire case workflow",
        _ => "Unknown"
    };

    private string CreateCaseRequest(WireTransfer wire, WireCase wireCase, Bank sender, Bank receiver)
    {
        if (wireCase.RequestMessageType == "camt.110")
            return wireMessages.Create("camt.110", wire, sender, receiver, null,
                wireCase.Reason).XmlPayload;
        var root = wireCase.RequestMessageType switch
        {
            "camt.056" => new XElement("FIToFIPmtCxlReq",
                Assignment(wireCase, sender, receiver),
                new XElement("Undrlyg",
                    OriginalPayment(wire),
                    new XElement("CxlRsnInf", new XElement("Rsn", new XElement("Prtry", "CUST")),
                        new XElement("AddtlInf", wireCase.Reason)))),
            "pacs.028" => new XElement("FIToFIPmtStsReq",
                new XElement("GrpHdr", new XElement("MsgId", wireCase.Id.ToString("N")),
                    new XElement("CreDtTm", wireCase.CreatedDate.UtcDateTime.ToString("O"))),
                new XElement("TxInf", OriginalPayment(wire), new XElement("StsReqRsn", wireCase.Reason))),
            _ => new XElement("ClmNonRct",
                Assignment(wireCase, sender, receiver),
                new XElement("Undrlyg", OriginalPayment(wire), new XElement("AddtlInf", wireCase.Reason)))
        };
        return iso.CreateMessage(wireCase.RequestMessageType, Endpoint(sender, wire.Rail),
            Endpoint(receiver, wire.Rail), root, wireCase.Id.ToString("N"), BusinessService(wire.Rail));
    }

    private void ResolveInvestigation(BankingDbContext db, WireTransfer wire, WireCase wireCase,
        Bank sender, Bank receiver)
    {
        wireCase.Status = WireCaseStatus.Resolved;
        wireCase.ResponseMessageType = WireCasePolicy.ResponseMessageType(wireCase.Type, wire.Rail);
        wireCase.Resolution = $"The simulated receiving institution confirmed the payment status is {wire.Status}; " +
            $"network reference {wire.Imad ?? wire.Omad ?? "not assigned"}.";
        wireCase.UpdatedDate = DateTimeOffset.UtcNow;
        var response = wireCase.ResponseMessageType == "pacs.002"
            ? iso.CreateFedNowPacs002(wire.CorrelationId, StatusCode(wire.Status), wireCase.Resolution,
                wire.Imad ?? wire.CorrelationId.ToString("N"), receiver.RoutingNumber, sender.RoutingNumber,
                wire.TransferType == WireTransferType.FinancialInstitutionCreditTransfer
                    ? "pacs.009.001.08" : "pacs.008.001.08")
            : CreateCamt029(wire, wireCase, receiver, sender, true);
        db.IsoMessages.Add(new IsoMessage { WireTransferId = wire.Id,
            MessageType = wireCase.ResponseMessageType, Direction = MessageDirection.Inbound,
            XmlPayload = response });
        db.WireEvents.Add(CaseEvent(wire.Id, "CaseResolved", wireCase.Resolution));
    }

    private async Task ResolveReturnRequestAsync(BankingDbContext db, WireTransfer wire,
        WireCase wireCase, Bank sender, Bank receiver, CancellationToken token)
    {
        var incoming = await db.WireTransfers.SingleOrDefaultAsync(x =>
            x.CorrelationId == wire.CorrelationId && x.Direction == WireDirection.Incoming
            && x.BankId == wire.ReceiverBankId, token);
        var origin = wire.FromAccountId is Guid fromId
            ? await db.Accounts.SingleOrDefaultAsync(x => x.Id == fromId, token) : null;
        var beneficiary = incoming?.ToAccountId is Guid toId
            ? await db.Accounts.SingleOrDefaultAsync(x => x.Id == toId, token) : null;
        var canReturn = WireReturnPosting.CanComplete(wire, incoming, origin, beneficiary, receiver);

        wireCase.ResponseMessageType = "camt.029";
        wireCase.UpdatedDate = DateTimeOffset.UtcNow;
        if (!canReturn)
        {
            wireCase.Status = WireCaseStatus.Rejected;
            wireCase.Resolution = "The simulated receiving institution rejected the request because the original payment or sufficient beneficiary funds were unavailable.";
            db.IsoMessages.Add(new IsoMessage { WireTransferId = wire.Id, MessageType = "camt.029",
                Direction = MessageDirection.Inbound,
                XmlPayload = CreateCamt029(wire, wireCase, receiver, sender, false) });
            db.WireEvents.Add(CaseEvent(wire.Id, "ReturnRejected", wireCase.Resolution));
            return;
        }

        var incomingWire = incoming!;
        WireReturnPosting.Complete(wire, incomingWire, origin, beneficiary, sender, receiver);
        wireCase.Status = WireCaseStatus.ReturnCompleted;
        wireCase.Resolution = "The receiving institution accepted the request and returned the full payment amount.";
        var response = CreateCamt029(wire, wireCase, receiver, sender, true);
        var paymentReturn = CreatePaymentReturn(wire, wireCase, receiver, sender);
        db.IsoMessages.AddRange(
            new IsoMessage { WireTransferId = wire.Id, MessageType = "camt.029",
                Direction = MessageDirection.Inbound, XmlPayload = response },
            new IsoMessage { WireTransferId = wire.Id, MessageType = "pacs.004",
                Direction = MessageDirection.Inbound, XmlPayload = paymentReturn },
            new IsoMessage { WireTransferId = incomingWire.Id, MessageType = "pacs.004",
                Direction = MessageDirection.Outbound, XmlPayload = paymentReturn });
        if (wire.TransferType == WireTransferType.FinancialInstitutionCreditTransfer)
            AddInstitutionReturnJournals(db, wire, incomingWire);
        else
            AddReturnJournals(db, wire, incomingWire, origin!, beneficiary!);
        db.WireEvents.Add(CaseEvent(wire.Id, "ReturnCompleted",
            wire.TransferType == WireTransferType.FinancialInstitutionCreditTransfer
                ? $"{wire.Amount:C} was returned to the sending bank's master-account liquidity."
                : $"{wire.Amount:C} was returned and re-credited to the originating customer account."));
        db.WireEvents.Add(CaseEvent(incomingWire.Id, "ReturnCompleted",
            wire.TransferType == WireTransferType.FinancialInstitutionCreditTransfer
                ? $"{wire.Amount:C} was debited from master-account liquidity and returned to the sending bank."
                : $"{wire.Amount:C} was debited from the beneficiary account and returned to the sending bank."));
    }

    private string CreateCamt029(WireTransfer wire, WireCase wireCase, Bank from, Bank to,
        bool accepted) => iso.CreateMessage("camt.029", Endpoint(from, wire.Rail), Endpoint(to, wire.Rail),
        new XElement("RsltnOfInvstgtn",
            Assignment(wireCase, from, to),
            new XElement("Sts", new XElement("Conf", accepted ? "ACCP" : "RJCR")),
            new XElement("CxlDtls", OriginalPayment(wire),
                new XElement("RsltnRltdInf", wireCase.Resolution))),
        Guid.NewGuid().ToString("N"), BusinessService(wire.Rail));

    private string CreatePaymentReturn(WireTransfer wire, WireCase wireCase, Bank from, Bank to) =>
        iso.CreateMessage("pacs.004", Endpoint(from, wire.Rail), Endpoint(to, wire.Rail),
            new XElement("PmtRtr",
                new XElement("GrpHdr", new XElement("MsgId", Guid.NewGuid().ToString("N")),
                    new XElement("CreDtTm", DateTime.UtcNow.ToString("O")), new XElement("NbOfTxs", 1)),
                new XElement("TxInf",
                    new XElement("RtrId", wireCase.Id.ToString("N")), OriginalPayment(wire),
                    new XElement("RtrdIntrBkSttlmAmt", new XAttribute("Ccy", "USD"),
                        wire.Amount.ToString("0.00", CultureInfo.InvariantCulture)),
                    new XElement("RtrRsnInf", new XElement("Rsn", new XElement("Prtry", "CUST")),
                        new XElement("AddtlInf", wireCase.Reason)))),
            Guid.NewGuid().ToString("N"), BusinessService(wire.Rail));

    private static XElement Assignment(WireCase wireCase, Bank from, Bank to) =>
        new("Assgnmt", new XElement("Id", wireCase.Id.ToString("N")),
            new XElement("Assgnr", from.Name), new XElement("Assgne", to.Name),
            new XElement("CreDtTm", wireCase.CreatedDate.UtcDateTime.ToString("O")));

    private static XElement OriginalPayment(WireTransfer wire) =>
        new("OrgnlTxRef", new XElement("OrgnlInstrId", wire.CorrelationId.ToString("N")),
            new XElement("OrgnlUETR", wire.CorrelationId.ToString().ToLowerInvariant()),
            new XElement("OrgnlIntrBkSttlmAmt", new XAttribute("Ccy", "USD"),
                wire.Amount.ToString("0.00", CultureInfo.InvariantCulture)));

    private static string Endpoint(Bank bank, PaymentRail rail) =>
        rail == PaymentRail.SwiftCbprPlus ? bank.Bic : bank.RoutingNumber;

    private static string BusinessService(PaymentRail rail) => rail switch
    {
        PaymentRail.FedNow => FedNowProfile.BusinessService,
        PaymentRail.SwiftCbprPlus => CbprPlusProfile.BusinessService,
        _ => "fedwire-lab"
    };

    private static string StatusCode(WireStatus status) => status switch
    {
        WireStatus.Rejected => "RJCT",
        WireStatus.PendingAtFed => "PDNG",
        _ => "ACSC"
    };

    private static string CaseLabel(WireCaseType type) =>
        type == WireCaseType.ReturnRequest ? "Return request" : "Investigation";

    private static WireEvent CaseEvent(Guid wireId, string type, string description) =>
        new() { WireTransferId = wireId, EventType = type, Description = description };

    private static void AddReturnJournals(BankingDbContext db, WireTransfer outgoing,
        WireTransfer incoming, Account origin, Account beneficiary)
    {
        var outgoingJournal = Guid.NewGuid();
        var incomingJournal = Guid.NewGuid();
        var outgoingSettlement = outgoing.Rail == PaymentRail.SwiftCbprPlus
            ? $"CORRESPONDENT:{outgoing.SenderBankId:N}" : $"FEDMASTER:{outgoing.SenderBankId:N}";
        var incomingSettlement = incoming.Rail == PaymentRail.SwiftCbprPlus
            ? $"CORRESPONDENT:{incoming.ReceiverBankId:N}" : $"FEDMASTER:{incoming.ReceiverBankId:N}";
        db.LedgerEntries.AddRange(
            ReturnEntry(outgoingJournal, outgoing.Id, outgoingSettlement, "Settlement cash",
                outgoing.Amount, 0, "Debit cash received for returned payment"),
            ReturnEntry(outgoingJournal, outgoing.Id, $"CUSTOMER:{origin.AccountNumber}", "Customer deposits",
                0, outgoing.Amount, "Credit originating customer for returned payment"),
            ReturnEntry(incomingJournal, incoming.Id, $"CUSTOMER:{beneficiary.AccountNumber}", "Customer deposits",
                incoming.Amount, 0, "Debit beneficiary for returned payment"),
            ReturnEntry(incomingJournal, incoming.Id, incomingSettlement, "Settlement cash",
                0, incoming.Amount, "Credit cash sent for returned payment"));
    }

    private static void AddInstitutionReturnJournals(BankingDbContext db, WireTransfer outgoing,
        WireTransfer incoming)
    {
        var outgoingJournal = Guid.NewGuid();
        var incomingJournal = Guid.NewGuid();
        db.LedgerEntries.AddRange(
            ReturnEntry(outgoingJournal, outgoing.Id, $"FEDMASTER:{outgoing.SenderBankId:N}",
                "Fed master account", outgoing.Amount, 0, "Debit returned master-account liquidity"),
            ReturnEntry(outgoingJournal, outgoing.Id, $"FI_CLEARING:{outgoing.ReceiverBankId:N}",
                "Financial institution transfer clearing", 0, outgoing.Amount,
                "Credit returned institution transfer clearing"),
            ReturnEntry(incomingJournal, incoming.Id, $"FI_CLEARING:{incoming.SenderBankId:N}",
                "Financial institution transfer clearing", incoming.Amount, 0,
                "Debit institution return clearing"),
            ReturnEntry(incomingJournal, incoming.Id, $"FEDMASTER:{incoming.ReceiverBankId:N}",
                "Fed master account", 0, incoming.Amount, "Credit returned master-account liquidity"));
    }

    private static LedgerEntry ReturnEntry(Guid journal, Guid wireId, string code, string name,
        decimal debit, decimal credit, string description) => new()
    {
        JournalId = journal, WireTransferId = wireId, AccountCode = code, AccountName = name,
        Debit = debit, Credit = credit, Description = description
    };

    private sealed record OpenCaseOutcome(bool NotFound, string? Error, string? Notice)
    {
        public static OpenCaseOutcome Missing() => new(true, null, null);
        public static OpenCaseOutcome Failed(string error) => new(false, error, null);
        public static OpenCaseOutcome Succeeded(string notice) => new(false, null, notice);
    }

    private static async Task<CreateWireViewModel> PopulateAsync(CreateWireViewModel model, Guid bankId,
        BankingDbContext db, CancellationToken token)
    {
        model.Accounts = await db.Accounts.Where(x => x.Customer.BankId == bankId)
            .Select(x => new SelectListItem($"{x.Customer.Name} · {x.AccountNumber} · available {(x.Balance - x.HeldBalance):C}", x.Id.ToString()))
            .ToListAsync(token);
        model.Banks = await db.Banks.Where(x => x.Id != bankId).OrderBy(x => x.Name)
            .Select(x => new SelectListItem($"{x.Name} · {x.CountryCode} · BIC {x.Bic}", x.Id.ToString()))
            .ToListAsync(token);
        model.Rails = Enum.GetValues<PaymentRail>().Where(x => x is PaymentRail.Fedwire
                or PaymentRail.FedNow or PaymentRail.SwiftCbprPlus)
            .Select(x => new SelectListItem(x == PaymentRail.SwiftCbprPlus
                ? "SWIFT international wire (CBPR+)" : x.ToString(), ((int)x).ToString())).ToList();
        var sender = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);
        model.SenderBankName = sender.Name;
        model.SenderMasterAccountBalance = sender.MasterAccountBalance;
        return model;
    }

    private static void AddExchangeMessages(MessageExchange exchange,
        IReadOnlyList<CreatedWireIsoMessage> messages)
    {
        exchange.Messages = messages.Select(x => new IsoMessage
        {
            MessageExchangeId = exchange.Id, MessageType = x.MessageType,
            Direction = x.Direction, XmlPayload = x.XmlPayload
        }).ToList();
    }

    private static string ReadPaymentAccount(string xml, string accountElement)
    {
        var account = XDocument.Parse(xml).Descendants()
            .First(x => x.Name.LocalName == accountElement);
        return account.Descendants().First(x => x.Name.LocalName == "Id" && !x.HasElements)
            .Value.Trim();
    }

    private static string ReadPaymentParty(string xml, string partyElement)
    {
        var party = XDocument.Parse(xml).Descendants()
            .First(x => x.Name.LocalName == partyElement);
        return party.Descendants().First(x => x.Name.LocalName == "Nm").Value.Trim();
    }

    private static string NormalizeDecisionReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? "Approver declined the payment."
            : reason.Trim().Length <= 500 ? reason.Trim() : reason.Trim()[..500];

    private static async Task<MessageWorkflowsViewModel> LoadMessageWorkflowsAsync(Guid bankId,
        BankingDbContext db, CancellationToken token)
    {
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);
        var accounts = await db.Accounts.Where(x => x.Customer.BankId == bankId)
            .OrderBy(x => x.Customer.Name)
            .Select(x => new SelectListItem($"{x.Customer.Name} · {x.AccountNumber}", x.Id.ToString()))
            .AsNoTracking().ToListAsync(token);
        var banks = await db.Banks.Where(x => x.Id != bankId).OrderBy(x => x.Name)
            .Select(x => new SelectListItem($"{x.Name} · {x.RoutingNumber}", x.Id.ToString()))
            .AsNoTracking().ToListAsync(token);
        var fedNowBanks = await db.Banks.Where(x => x.Id != bankId && x.FedNowEnabled && x.FedNowOnline)
            .OrderBy(x => x.Name)
            .Select(x => new SelectListItem($"{x.Name} · {x.RoutingNumber}", x.Id.ToString()))
            .AsNoTracking().ToListAsync(token);
        var exchanges = await db.MessageExchanges
            .Where(x => x.BankId == bankId || x.CounterpartyBankId == bankId)
            .Include(x => x.Bank).Include(x => x.CounterpartyBank).Include(x => x.Messages)
            .OrderByDescending(x => x.CreatedDate).AsSplitQuery().AsNoTracking()
            .Take(30).ToListAsync(token);
        return new MessageWorkflowsViewModel
        {
            Bank = bank, Accounts = accounts, Banks = banks, FedNowBanks = fedNowBanks,
            Exchanges = exchanges
        };
    }
}
