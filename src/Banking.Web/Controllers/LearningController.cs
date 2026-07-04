using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Controllers;

[Authorize]
public sealed class LearningController(IDbContextFactory<BankingDbContext> dbFactory,
    LearningEvidenceService evidenceService) : Controller
{
    private string LearnerAlias => HttpContext.Session.GetString("learning_alias") ?? "Bank team";

    public async Task<IActionResult> Index(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token); var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var completed = (await db.LearningProgress.Where(x => x.BankId == bankId && x.IsComplete).Select(x => x.MissionId).ToListAsync(token)).ToHashSet();
        ViewData["LearnerAlias"] = LearnerAlias;
        return View(new LearningCenterViewModel(LearningCatalog.Paths, LearningCatalog.Missions, completed));
    }

    public async Task<IActionResult> Mission(string id, CancellationToken token)
    {
        var mission = LearningCatalog.Mission(id);
        if (mission is null) return NotFound();
        await using var db = await dbFactory.CreateDbContextAsync(token); var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var progress = await db.LearningProgress.AsNoTracking().SingleOrDefaultAsync(x => x.BankId == bankId && x.MissionId == mission.Id, token);
        var attempt = await db.LearningAttempts.AsNoTracking().Where(x => x.BankId == bankId && x.MissionId == mission.Id).OrderByDescending(x => x.CreatedDate).FirstOrDefaultAsync(token);
        var attemptCount = await db.LearningAttempts.CountAsync(x => x.BankId == bankId && x.MissionId == mission.Id, token);
        var path = LearningCatalog.Paths.Single(x => x.Id == mission.PathId);
        var run = await evidenceService.EvaluateLatestAsync(bankId, mission.Id, token);
        return View(new MissionPageViewModel(mission, path, progress?.IsComplete == true, progress?.Prediction,
            LearningCatalog.Hints(id), LearningCatalog.RoleHandoffs(id), LearningCatalog.Question(id, attemptCount), attempt,
            LearningCatalog.ProductionNote(id), run, progress?.NextReviewDate));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetLearner(string learnerAlias, CancellationToken token)
    {
        var alias = learnerAlias.Trim();
        if (alias.Length is < 2 or > 80)
        {
            TempData["Error"] = "Enter a learner or cohort name between 2 and 80 characters.";
            return RedirectToAction(nameof(Index));
        }
        await using var db = await dbFactory.CreateDbContextAsync(token);
        _ = await ActiveBank.ResolveAsync(HttpContext, db, token);
        HttpContext.Session.SetString("learning_alias", alias);
        TempData["Notice"] = $"Learning evidence will be attributed to {alias}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> StartRun(string id, CancellationToken token)
    {
        var mission = LearningCatalog.Mission(id);
        if (mission is null) return NotFound();
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var accounts = await db.Accounts.Where(x => x.Customer.BankId == bankId)
            .Select(x => new { x.Balance, x.HeldBalance }).ToListAsync(token);
        var master = await db.Banks.Where(x => x.Id == bankId)
            .Select(x => x.MasterAccountBalance).SingleAsync(token);
        var progress = await db.LearningProgress.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BankId == bankId && x.MissionId == id, token);
        db.LearningRuns.Add(new LearningRun
        {
            BankId = bankId, MissionId = id, LearnerAlias = LearnerAlias,
            Rail = mission.Rail?.ToString() ?? MissionRail(id), Prediction = progress?.Prediction,
            BaselineCustomerBalance = accounts.Sum(x => x.Balance),
            BaselineHeldBalance = accounts.Sum(x => x.HeldBalance), BaselineMasterBalance = master
        });
        await db.SaveChangesAsync(token);
        return RedirectToAction(mission.ActionName, mission.ActionController,
            new { learningMission = mission.Id, rail = mission.Rail, scenario = mission.Scenario });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Predict(string id, string prediction, CancellationToken token)
    {
        var mission = LearningCatalog.Mission(id);
        if (mission is null) return NotFound();
        if (!mission.PredictionChoices.Contains(prediction))
        {
            TempData["Error"] = "Choose one of the available predictions.";
            return RedirectToAction(nameof(Mission), new { id });
        }
        await using var db = await dbFactory.CreateDbContextAsync(token); var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var progress = await ProgressAsync(db, bankId, mission.Id, token); progress.Prediction = prediction; progress.UpdatedDate = DateTimeOffset.UtcNow; await db.SaveChangesAsync(token);
        TempData["Notice"] = "Prediction saved. Run the exercise, then compare it with the evidence.";
        return RedirectToAction(nameof(Mission), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete(string id, Guid runId, CancellationToken token)
    {
        var mission = LearningCatalog.Mission(id);
        if (mission is null) return NotFound();
        await using var db = await dbFactory.CreateDbContextAsync(token); var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var progress = await ProgressAsync(db, bankId, mission.Id, token);
        var run = await evidenceService.EvaluateAsync(bankId, runId, token);
        if (progress.Prediction is null || progress.AnsweredQuestions == 0 || run?.CanComplete != true)
        {
            TempData["Error"] = "Record a prediction, answer the knowledge check, and satisfy every simulator evidence check before completing the mission.";
            return RedirectToAction(nameof(Mission), new { id });
        }
        var entity = await db.LearningRuns.SingleAsync(x => x.Id == runId && x.BankId == bankId, token);
        entity.Status = "Completed"; entity.CompletedDate = entity.UpdatedDate = DateTimeOffset.UtcNow;
        progress.IsComplete = true; progress.UpdatedDate = DateTimeOffset.UtcNow; await db.SaveChangesAsync(token);
        TempData["Notice"] = $"Mission completed: {mission.Title}.";
        return RedirectToAction(nameof(Mission), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Answer(string id, string questionId, string answer, int confidence = 3,
        CancellationToken token = default)
    {
        var mission = LearningCatalog.Mission(id); if (mission is null) return NotFound();
        var variant = questionId.EndsWith("-evidence", StringComparison.Ordinal) ? 1 : 0;
        var question = LearningCatalog.Question(id, variant);
        if (question.Id != questionId) return BadRequest();
        if (!question.Choices.Contains(answer)) return RedirectToAction(nameof(Mission), new { id });
        await using var db = await dbFactory.CreateDbContextAsync(token); var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var correct = answer == question.CorrectAnswer;
        db.LearningAttempts.Add(new() { BankId = bankId, MissionId = id, QuestionId = question.Id,
            SelectedAnswer = answer, IsCorrect = correct, LearnerAlias = LearnerAlias,
            Confidence = Math.Clamp(confidence, 1, 5) });
        var progress = await ProgressAsync(db, bankId, id, token); progress.AnsweredQuestions++; if (correct) progress.CorrectAnswers++;
        progress.NextReviewDate = DateTimeOffset.UtcNow.AddDays(correct ? 7 : 1); progress.UpdatedDate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token); TempData[correct ? "Notice" : "Error"] = question.OptionFeedback[answer];
        return RedirectToAction(nameof(Mission), new { id });
    }

    public async Task<IActionResult> Challenges(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var live = await db.WireTransfers.Where(x => x.BankId == bankId)
            .OrderByDescending(x => x.CreatedDate).Take(8)
            .Select(x => new { x.Id, x.Status, x.Scenario, MessageCount = x.IsoMessages.Count,
                JournalCount = db.LedgerEntries.Count(j => j.WireTransferId == x.Id) })
            .AsNoTracking().ToListAsync(token);
        var challenges = live.Select(x => new DiagnosticChallengeViewModel($"wire-{x.Id}",
            $"Live wire {x.Id.ToString()[..8]}",
            $"Status {x.Status}; scenario {x.Scenario}; {x.MessageCount} ISO messages; {x.JournalCount} journal lines.",
            ["Transport still in progress", "Business-rule rejection", "Final settlement", "Control failure"],
            x.Status == WireStatus.Rejected ? "Business-rule rejection"
                : x.Status is WireStatus.Settled or WireStatus.Completed ? "Final settlement" : "Transport still in progress",
            "Open the correlated item and reconcile its timeline, status messages, holds, and journal.", x.Id, "Wire")).ToList();
        if (challenges.Count == 0)
            challenges.Add(new("no-live-items", "No live evidence yet", "Create a payment, then return here for a generated evidence packet.",
                ["Create a simulator item", "Reveal a memorized answer"], "Create a simulator item", "Diagnostic cases are generated from bank-scoped durable records."));
        return View(challenges);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Diagnose(string id, string answer,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var correct = false;
        if (id.StartsWith("wire-", StringComparison.Ordinal) && Guid.TryParse(id[5..], out var wireId))
        {
            var status = await db.WireTransfers.Where(x => x.Id == wireId && x.BankId == bankId)
                .Select(x => (WireStatus?)x.Status).SingleOrDefaultAsync(token);
            var expected = status == WireStatus.Rejected ? "Business-rule rejection"
                : status is WireStatus.Settled or WireStatus.Completed ? "Final settlement" : "Transport still in progress";
            correct = answer == expected;
        }
        db.LearningAttempts.Add(new LearningAttempt { BankId = bankId, MissionId = "diagnostics",
            QuestionId = id.Length > 40 ? id[..40] : id, SelectedAnswer = answer,
            IsCorrect = correct, LearnerAlias = LearnerAlias, Confidence = 3 });
        await db.SaveChangesAsync(token);
        TempData[correct ? "Notice" : "Error"] = correct
            ? "Diagnosis supported. Verify it against the correlated timeline and journal."
            : "The evidence does not support that diagnosis yet. Reconcile transport, business status, settlement, and accounting separately.";
        return RedirectToAction(nameof(Challenges));
    }

    public async Task<IActionResult> Workbench(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var ids = await db.LearningRuns.Where(x => x.BankId == bankId)
            .OrderByDescending(x => x.StartedDate).Select(x => x.Id).Take(12).ToListAsync(token);
        var runs = new List<LearningRunViewModel>();
        foreach (var id in ids)
        {
            var run = await evidenceService.EvaluateAsync(bankId, id, token);
            if (run is not null) runs.Add(run);
        }
        return View(new LearningWorkbenchViewModel(runs));
    }

    public IActionResult FaultLab() => View();

    public IActionResult Branching() => View(new[]
    {
        new DiagnosticChallengeViewModel("malformed-before-send", "Malformed instruction stopped before transport", "Profile validation failed; no delivery exists; no hold or journal exists.", ["Retry unchanged", "Repair and resubmit through approval", "Return settled funds", "Replay a dead letter"], "Repair and resubmit through approval", "A structurally invalid instruction must be repaired and pass the authorized workflow before transport."),
        new DiagnosticChallengeViewModel("delivered-rejected", "Delivered instruction received RJCT", "Transport delivery succeeded; the final business status is rejected; the hold was released.", ["Treat delivery as settlement", "Investigate the reason and repair only if permitted", "Post a manual debit", "Replay until accepted"], "Investigate the reason and repair only if permitted", "Delivery cannot override a business rejection. Use the reason and governing workflow to determine repairability."),
        new DiagnosticChallengeViewModel("settled-return", "Customer requests funds back after final settlement", "The original payment is final and has a balanced journal.", ["Delete the journal", "Change the status to rejected", "Initiate the rail-appropriate return/request workflow", "Republish the original instruction"], "Initiate the rail-appropriate return/request workflow", "A settled payment is not erased; any return is a separately authorized and auditable value movement."),
        new DiagnosticChallengeViewModel("unknown-pending", "Expected final response has not arrived", "Delivery succeeded and only an intermediate pending status exists.", ["Post settlement based on elapsed time", "Investigate using correlated identifiers and escalate on timeout", "Create a duplicate payment", "Release every hold"], "Investigate using correlated identifiers and escalate on timeout", "Pending is not final. Correlation, timeout policy, reconciliation, and escalation determine the next action.")
    });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DecideBranch(string id, string answer,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var expected = id switch
        {
            "malformed-before-send" => "Repair and resubmit through approval",
            "delivered-rejected" => "Investigate the reason and repair only if permitted",
            "settled-return" => "Initiate the rail-appropriate return/request workflow",
            "unknown-pending" => "Investigate using correlated identifiers and escalate on timeout",
            _ => null
        };
        if (expected is null) return BadRequest();
        var correct = answer == expected;
        db.LearningAttempts.Add(new LearningAttempt { BankId = bankId, MissionId = "branching",
            QuestionId = id, SelectedAnswer = answer, IsCorrect = correct,
            LearnerAlias = LearnerAlias, Confidence = 3 });
        await db.SaveChangesAsync(token);
        TempData[correct ? "Notice" : "Error"] = correct
            ? "Decision preserves payment state and auditability."
            : "That action conflicts with the observed processing boundary or accounting state.";
        return RedirectToAction(nameof(Branching));
    }

    public IActionResult RailCases() => View(new[]
    {
        new RailDecisionCaseViewModel("instant", "A domestic payment must be available in seconds and final immediately.", ["ACH", "FedNow", "Check"], "FedNow", "FedNow combines immediate receiver processing with final settlement, subject to participation and limits."),
        new RailDecisionCaseViewModel("payroll", "Ten thousand payroll credits are scheduled for the next banking day.", ["Fedwire", "ACH", "SWIFT CBPR+"], "ACH", "Batch grouping and effective dates fit high-volume scheduled credits."),
        new RailDecisionCaseViewModel("cross-border", "A USD customer payment must reach an overseas bank through correspondents.", ["FedNow", "Check", "SWIFT CBPR+"], "SWIFT CBPR+", "SWIFT carries the cross-border message while correspondent arrangements model settlement."),
        new RailDecisionCaseViewModel("paper", "A deposited paper item must be presented using captured front and back images.", ["Check", "ACH", "Fedwire"], "Check", "The image check workflow uses image cash letters and paying-bank presentment.")
    });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DecideRail(string id, string answer, int confidence = 3,
        CancellationToken token = default)
    {
        var expected = id switch { "instant" => "FedNow", "payroll" => "ACH",
            "cross-border" => "SWIFT CBPR+", "paper" => "Check", _ => null };
        if (expected is null) return BadRequest();
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var correct = answer == expected;
        db.LearningAttempts.Add(new LearningAttempt { BankId = bankId, MissionId = "rail-cases",
            QuestionId = id, SelectedAnswer = answer, IsCorrect = correct,
            LearnerAlias = LearnerAlias, Confidence = Math.Clamp(confidence, 1, 5) });
        await db.SaveChangesAsync(token);
        TempData[correct ? "Notice" : "Error"] = correct
            ? $"{expected} fits the stated requirement."
            : "Recheck urgency, geography, finality, format, and settlement requirements.";
        return RedirectToAction(nameof(RailCases));
    }

    public IActionResult Glossary() => View();

    [Authorize(Roles = BankSecurity.Operations)]
    public async Task<IActionResult> Instructor(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var progress = await db.LearningProgress.Include(x => x.Bank).AsNoTracking().ToListAsync(token);
        var banks = progress.GroupBy(x => x.Bank.Name).Select(g => new InstructorBankViewModel(g.Key, g.Count(), g.Count(x => x.IsComplete), g.Sum(x => x.CorrectAnswers), g.Sum(x => x.AnsweredQuestions), g.Max(x => (DateTimeOffset?)x.UpdatedDate))).OrderBy(x => x.Bank).ToList();
        var attempts = await db.LearningAttempts.Include(x => x.Bank).OrderByDescending(x => x.CreatedDate).Take(30).AsNoTracking().ToListAsync(token);
        var runs = await db.LearningRuns.Include(x => x.Bank).OrderByDescending(x => x.StartedDate).Take(50).AsNoTracking().ToListAsync(token);
        var assignments = await db.LearningAssignments.Include(x => x.Bank).Where(x => x.IsActive)
            .OrderBy(x => x.DueDate).AsNoTracking().ToListAsync(token);
        return View(new InstructorDashboardViewModel(banks, attempts, LearningCatalog.Missions, runs, assignments));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.Operations)]
    public async Task<IActionResult> Assign(string missionId, string cohort, DateTimeOffset? dueDate,
        CancellationToken token)
    {
        if (LearningCatalog.Mission(missionId) is null || string.IsNullOrWhiteSpace(cohort)) return BadRequest();
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        db.LearningAssignments.Add(new LearningAssignment { BankId = bankId, MissionId = missionId,
            Cohort = cohort.Trim()[..Math.Min(cohort.Trim().Length, 80)], DueDate = dueDate,
            AssignedBy = User.Identity?.Name ?? "Operations" });
        await db.SaveChangesAsync(token);
        TempData["Notice"] = "Mission assigned to the cohort.";
        return RedirectToAction(nameof(Instructor));
    }

    [Authorize(Roles = BankSecurity.Operations)]
    public async Task<IActionResult> ExportResults(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var rows = await db.LearningRuns.Include(x => x.Bank).OrderBy(x => x.Bank.Name).ThenBy(x => x.MissionId).ThenBy(x => x.StartedDate).AsNoTracking().ToListAsync(token);
        var csv = new System.Text.StringBuilder("Bank,LearnerOrCohort,Mission,Run,ItemType,ItemId,Status,EvidenceScore,Started,Completed\r\n");
        static string Cell(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
        foreach (var row in rows) csv.AppendLine(string.Join(',', Cell(row.Bank.Name), Cell(row.LearnerAlias),
            Cell(row.MissionId), row.Id, Cell(row.ItemType ?? ""), row.ItemId, Cell(row.Status), row.EvidenceScore,
            Cell(row.StartedDate.ToString("O")), Cell(row.CompletedDate?.ToString("O") ?? "")));
        return File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"learning-results-{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetProgress(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token); var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        db.LearningAttempts.RemoveRange(db.LearningAttempts.Where(x => x.BankId == bankId));
        db.LearningRuns.RemoveRange(db.LearningRuns.Where(x => x.BankId == bankId));
        db.LearningProgress.RemoveRange(db.LearningProgress.Where(x => x.BankId == bankId)); await db.SaveChangesAsync(token);
        TempData["Notice"] = "Learning progress reset. Payment and accounting records were not changed."; return RedirectToAction(nameof(Index));
    }

    public IActionResult Rails() => View(new RailComparisonViewModel(
    [
        new("Fedwire", "Urgent domestic high-value transfers", "Same business day", "Final when settled", "No lab customer limit", "ISO 20022", "Federal Reserve master accounts", "Approval and delivery are not settlement"),
        new("FedNow", "Immediate domestic payments", "Seconds", "Immediate and final", "$10 million customer transfer", "ISO 20022", "Immediate Federal Reserve settlement", "Receiver availability is part of routing"),
        new("SWIFT CBPR+", "Cross-border bank messaging", "Depends on route", "Depends on settlement arrangement", "No lab-specific limit", "ISO 20022 / CBPR+", "Simulated correspondent positions", "SWIFT messaging is not itself settlement"),
        new("ACH", "High-volume scheduled consumer and business payments", "Batch / banking day", "Return rights remain", "Rules and risk limits vary", "94-character NACHA records", "FedACH-style net settlement", "Files, batches, entries, and returns are separate layers"),
        new("Checks", "Image-based presentment", "Batch / clearing cycle", "Subject to returns", "Policy dependent", "TIFF + image cash letter", "Paying/depository bank settlement", "Image validation is distinct from payment authorization")
    ]));

    public async Task<IActionResult> IsoExplorer(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var messages = await db.IsoMessages
            .Where(x => x.WireTransfer != null && x.WireTransfer.BankId == bankId)
            .OrderByDescending(x => x.CreatedDate).Take(12)
            .Select(x => new IsoExplorerMessageViewModel(x.Id, x.WireTransferId!.Value,
                x.MessageType, x.Direction, x.XmlPayload, x.CreatedDate))
            .AsNoTracking().ToListAsync(token);
        return View(new IsoExplorerViewModel(messages, Fields, MessageTypes));
    }

    private static async Task<LearningProgress> ProgressAsync(BankingDbContext db, Guid bankId, string missionId, CancellationToken token)
    {
        var progress = await db.LearningProgress.SingleOrDefaultAsync(x => x.BankId == bankId && x.MissionId == missionId, token);
        if (progress is not null) return progress; progress = new() { BankId = bankId, MissionId = missionId }; db.LearningProgress.Add(progress); return progress;
    }

    private static string MissionRail(string id) => id.StartsWith("ach-", StringComparison.Ordinal) ? "ACH"
        : id.StartsWith("check-", StringComparison.Ordinal) ? "Check"
        : id == "nonvalue-request" ? "ISO non-value" : "Cross-rail";

    private static readonly IReadOnlyList<IsoFieldDefinitionViewModel> Fields =
    [
        new("BizMsgIdr", "Business message ID", "Uniquely identifies this message instance."),
        new("MsgDefIdr", "Message definition", "Identifies the ISO message family and version."),
        new("EndToEndId", "End-to-end ID", "Customer reference carried through the payment chain."),
        new("UETR", "Unique end-to-end reference", "Globally unique tracking reference for the transaction."),
        new("IntrBkSttlmAmt", "Interbank settlement amount", "Amount transferred between financial institutions."),
        new("SttlmMtd", "Settlement method", "Specifies how interbank settlement is performed."),
        new("TxSts", "Transaction status", "Reports accepted, pending, completed, or rejected processing state."),
        new("OrgnlMsgId", "Original message ID", "Correlates a response with the message being answered.")
    ];

    private static readonly IReadOnlyList<MessageTypeComparisonViewModel> MessageTypes =
    [
        new("pacs.008", "Customer credit transfer", "Debtor agent", "pacs.002", "Yes, when accepted and settled"),
        new("pacs.009", "Financial institution credit transfer", "Sending institution", "pacs.002", "Yes, when accepted and settled"),
        new("pacs.002", "Payment processing status", "Network or instructed agent", "Usually none", "No"),
        new("pacs.004", "Payment return", "Returning institution", "pacs.002", "Yes, reverses value when accepted"),
        new("camt.056", "Payment cancellation request", "Requesting institution", "camt.029", "No; requests action"),
        new("pacs.028", "Status investigation", "Requesting institution", "pacs.002", "No")
    ];
}
