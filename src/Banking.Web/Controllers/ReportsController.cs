using System.Text;
using System.Text.Json;
using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Controllers;

[Authorize(Roles = BankSecurity.Operations)]
public sealed class ReportsController(IDbContextFactory<BankingDbContext> factory, BankReportJobService jobs) : Controller
{
    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(BankReportKind? kind, DateOnly? from, DateOnly? to, int page = 1, CancellationToken token = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = from ?? today.AddDays(-30); var end = to ?? today;
        if (start > end || kind is { } filter && !Enum.IsDefined(filter)) return BadRequest("Choose a valid report type and date range.");
        page = Math.Clamp(page, 1, 10000);
        await using var db = await factory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);
        var tasks = await db.BankReportTasks.AsNoTracking().Where(x => x.BankId == bankId).OrderBy(x => x.Name).ToListAsync(token);
        var fromUtc = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        // Use an inclusive end date without risking DateOnly.MaxValue.AddDays overflow.
        var untilUtc = new DateTimeOffset(end.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);
        var query = db.BankReportRuns.AsNoTracking().Where(x => x.BankId == bankId && x.RequestedAt >= fromUtc && x.RequestedAt <= untilUtc);
        if (kind is { } value) query = query.Where(x => x.Kind == value);
        var total = await query.CountAsync(token);
        var runs = await query.OrderByDescending(x => x.RequestedAt).Skip((page - 1) * 25).Take(25)
            .Select(x => new BankReportRun { Id = x.Id, BankId = x.BankId, TaskId = x.TaskId, TaskName = x.TaskName,
                Kind = x.Kind, LookbackHours = x.LookbackHours, Status = x.Status, RequestedBy = x.RequestedBy,
                RequestedAt = x.RequestedAt, ScheduledFor = x.ScheduledFor, StartedAt = x.StartedAt,
                CompletedAt = x.CompletedAt, Attempts = x.Attempts, Error = x.Error }).ToListAsync(token);
        var banks = await db.Banks.AsNoTracking().OrderBy(x => x.Name).ToListAsync(token);
        return View(new ReportLibraryViewModel(bank, banks, tasks, runs, kind, start, end, page, total));
    }

    [HttpGet]
    public async Task<IActionResult> Task(Guid? id, BankReportKind? kind, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        if (id is null)
        {
            var selected = kind is { } value && Enum.IsDefined(value) ? value : BankReportKind.PaymentActivity;
            return View(new ReportTaskForm { Name = ReportSchedulePolicy.Title(selected), Kind = selected, Frequency = ReportSchedulePolicy.IsOperational(selected) ? ReportSchedulePolicy.DefaultFrequency(selected) : ReportFrequency.Daily });
        }
        var task = await db.BankReportTasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.BankId == bankId, token);
        if (task is null) return NotFound();
        return View(new ReportTaskForm { Id = task.Id, Name = task.Name, Kind = task.Kind, Frequency = task.Frequency,
            DailyAtUtc = task.DailyAtUtc, LookbackHours = task.LookbackHours, Enabled = task.Enabled });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(ReportTaskForm model, CancellationToken token)
    {
        if (!ModelState.IsValid) return View("Task", model);
        await using var db = await factory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        try
        {
            await jobs.SaveTaskAsync(bankId, model.Id, model.Name, model.Kind, model.Frequency, model.DailyAtUtc,
                model.LookbackHours, model.Enabled, User.Identity?.Name ?? "Operations", token);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException exception) { ModelState.AddModelError("", exception.Message); return View("Task", model); }
        TempData["Notice"] = "Report task saved. Run now is available whether its schedule is enabled or paused.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> InstallJobs(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var count = await jobs.InstallOperationalTasksAsync(bankId, User.Identity?.Name ?? "Operations", token);
        TempData["Notice"] = $"Added {count} operational task(s) for this bank, paused and ready for Run now. Existing schedules and run history were preserved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RunNow(Guid id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        try
        {
            var runId = await jobs.RequestAsync(bankId, id, User.Identity?.Name ?? "Operations", token);
            return RedirectToAction(nameof(Details), new { id = runId });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Details(Guid id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var run = await db.BankReportRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.BankId == bankId, token);
        if (run is null) return NotFound();
        var bankName = await db.Banks.Where(x => x.Id == bankId).Select(x => x.Name).SingleAsync(token);
        var report = run.Status != ReportRunStatus.Succeeded || run.ReportJson is null ? null : JsonSerializer.Deserialize<BankReportDocument>(run.ReportJson);
        return View(new SavedReportViewModel(run, report, bankName));
    }

    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Status(Guid id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var run = await db.BankReportRuns.AsNoTracking().Where(x => x.Id == id && x.BankId == bankId)
            .Select(x => new { x.Status, x.CompletedAt }).SingleOrDefaultAsync(token);
        return run is null ? NotFound() : Json(new { status = run.Status.ToString(), complete = run.CompletedAt != null });
    }

    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Download(Guid id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var run = await db.BankReportRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.BankId == bankId, token);
        if (run?.Status != ReportRunStatus.Succeeded || run.ReportJson is null) return NotFound();
        return File(Encoding.UTF8.GetBytes(run.ReportJson), "application/json", $"bank-report-{run.Id:N}.json");
    }
}
