using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web;

public static class LearningRunLinker
{
    private const string SessionKey = "active_learning_run";

    public static void SetActive(HttpContext context, Guid runId) =>
        context.Session.SetString(SessionKey, runId.ToString());

    public static async Task AttachAsync(HttpContext context, BankingDbContext db, Guid bankId,
        string itemType, Guid itemId, CancellationToken token)
    {
        if (!Guid.TryParse(context.Session.GetString(SessionKey), out var runId)) return;
        var run = await db.LearningRuns.SingleOrDefaultAsync(x => x.Id == runId && x.BankId == bankId
            && x.ItemId == null && x.Status == "Started", token);
        if (run is null) return;
        run.ItemType = itemType;
        run.ItemId = itemId;
        run.UpdatedDate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
        context.Session.Remove(SessionKey);
    }
}
