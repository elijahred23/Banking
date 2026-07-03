using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Controllers;

[Authorize]
public sealed class LearningController(IDbContextFactory<BankingDbContext> dbFactory) : Controller
{
    public async Task<IActionResult> Index(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token); var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var completed = (await db.LearningProgress.Where(x => x.BankId == bankId && x.IsComplete).Select(x => x.MissionId).ToListAsync(token)).ToHashSet();
        return View(new LearningCenterViewModel(LearningCatalog.Paths, LearningCatalog.Missions, completed));
    }

    public async Task<IActionResult> Mission(string id, CancellationToken token)
    {
        var mission = LearningCatalog.Mission(id);
        if (mission is null) return NotFound();
        await using var db = await dbFactory.CreateDbContextAsync(token); var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var progress = await db.LearningProgress.AsNoTracking().SingleOrDefaultAsync(x => x.BankId == bankId && x.MissionId == mission.Id, token);
        var attempt = await db.LearningAttempts.AsNoTracking().Where(x => x.BankId == bankId && x.MissionId == mission.Id).OrderByDescending(x => x.CreatedDate).FirstOrDefaultAsync(token);
        var path = LearningCatalog.Paths.Single(x => x.Id == mission.PathId);
        return View(new MissionPageViewModel(mission, path, progress?.IsComplete == true, progress?.Prediction,
            LearningCatalog.Hints(id), LearningCatalog.RoleHandoffs(id), LearningCatalog.Question(id), attempt, LearningCatalog.ProductionNote(id)));
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
    public async Task<IActionResult> Complete(string id, CancellationToken token)
    {
        var mission = LearningCatalog.Mission(id);
        if (mission is null) return NotFound();
        await using var db = await dbFactory.CreateDbContextAsync(token); var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var progress = await ProgressAsync(db, bankId, mission.Id, token);
        if (progress.Prediction is null || progress.AnsweredQuestions == 0)
        {
            TempData["Error"] = "Record a prediction and answer the knowledge check before completing the mission.";
            return RedirectToAction(nameof(Mission), new { id });
        }
        progress.IsComplete = true; progress.UpdatedDate = DateTimeOffset.UtcNow; await db.SaveChangesAsync(token);
        TempData["Notice"] = $"Mission completed: {mission.Title}.";
        return RedirectToAction(nameof(Mission), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Answer(string id, string answer, CancellationToken token)
    {
        var mission = LearningCatalog.Mission(id); if (mission is null) return NotFound(); var question = LearningCatalog.Question(id);
        if (!question.Choices.Contains(answer)) return RedirectToAction(nameof(Mission), new { id });
        await using var db = await dbFactory.CreateDbContextAsync(token); var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var correct = answer == question.CorrectAnswer;
        db.LearningAttempts.Add(new() { BankId = bankId, MissionId = id, QuestionId = question.Id, SelectedAnswer = answer, IsCorrect = correct });
        var progress = await ProgressAsync(db, bankId, id, token); progress.AnsweredQuestions++; if (correct) progress.CorrectAnswers++; progress.UpdatedDate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token); TempData[correct ? "Notice" : "Error"] = correct ? $"Correct. {question.Explanation}" : $"Not yet. {question.Explanation}";
        return RedirectToAction(nameof(Mission), new { id });
    }

    public IActionResult Challenges() => View(new[]
    {
        new DiagnosticChallengeViewModel("transport-v-business", "Delivered but rejected", "RabbitMQ delivery is Delivered; pacs.002 is RJCT; no journal exists.", ["RabbitMQ outage", "Business-rule rejection", "Unbalanced journal"], "Business-rule rejection", "Read the status reason and verify no settlement journal posted."),
        new DiagnosticChallengeViewModel("stuck-pending", "Payment remains pending", "A PDNG status exists and the timeline has no final status.", ["Final settlement", "Expected intermediate state", "Duplicate posting"], "Expected intermediate state", "Wait for or trace the correlated final pacs.002."),
        new DiagnosticChallengeViewModel("hold-released", "Available balance restored", "A rejected outgoing wire has no ledger entries and held balance returned to zero.", ["Silent settlement", "Correct rejection handling", "Missing account"], "Correct rejection handling", "Reconcile balance, held balance, status, and journal together.")
    });

    public IActionResult Glossary() => View();

    [Authorize(Roles = BankSecurity.Operations)]
    public async Task<IActionResult> Instructor(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var progress = await db.LearningProgress.Include(x => x.Bank).AsNoTracking().ToListAsync(token);
        var banks = progress.GroupBy(x => x.Bank.Name).Select(g => new InstructorBankViewModel(g.Key, g.Count(), g.Count(x => x.IsComplete), g.Sum(x => x.CorrectAnswers), g.Sum(x => x.AnsweredQuestions), g.Max(x => (DateTimeOffset?)x.UpdatedDate))).OrderBy(x => x.Bank).ToList();
        var attempts = await db.LearningAttempts.Include(x => x.Bank).OrderByDescending(x => x.CreatedDate).Take(30).AsNoTracking().ToListAsync(token);
        return View(new InstructorDashboardViewModel(banks, attempts, LearningCatalog.Missions));
    }

    [Authorize(Roles = BankSecurity.Operations)]
    public async Task<IActionResult> ExportResults(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var rows = await db.LearningProgress.Include(x => x.Bank).OrderBy(x => x.Bank.Name).ThenBy(x => x.MissionId).AsNoTracking().ToListAsync(token);
        var csv = new System.Text.StringBuilder("Bank,Mission,Complete,Correct,Answered,Updated\r\n");
        static string Cell(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
        foreach (var row in rows) csv.AppendLine(string.Join(',', Cell(row.Bank.Name), Cell(row.MissionId), row.IsComplete, row.CorrectAnswers, row.AnsweredQuestions, Cell(row.UpdatedDate.ToString("O"))));
        return File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"learning-results-{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetProgress(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token); var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        db.LearningAttempts.RemoveRange(db.LearningAttempts.Where(x => x.BankId == bankId)); db.LearningProgress.RemoveRange(db.LearningProgress.Where(x => x.BankId == bankId)); await db.SaveChangesAsync(token);
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
