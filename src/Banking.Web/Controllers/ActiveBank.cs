using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Controllers;

internal static class ActiveBank
{
    public static async Task<Guid> ResolveAsync(HttpContext context, BankingDbContext db, CancellationToken token)
    {
        var id = context.User.BankId();
        if (await db.Banks.AnyAsync(x => x.Id == id, token)) return id;
        throw new InvalidOperationException("The signed-in user's bank no longer exists.");
    }
}
