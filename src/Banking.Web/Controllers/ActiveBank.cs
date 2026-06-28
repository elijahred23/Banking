using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Controllers;

internal static class ActiveBank
{
    private const string Key = "ActiveBankId";
    public static async Task<Guid> ResolveAsync(HttpContext context, BankingDbContext db, CancellationToken token)
    {
        if (Guid.TryParse(context.Session.GetString(Key), out var id)
            && await db.Banks.AnyAsync(x => x.Id == id, token)) return id;
        id = await db.Banks.OrderBy(x => x.Name).Select(x => x.Id).FirstAsync(token);
        context.Session.SetString(Key, id.ToString());
        return id;
    }

    public static void Set(HttpContext context, Guid bankId) => context.Session.SetString(Key, bankId.ToString());
}
