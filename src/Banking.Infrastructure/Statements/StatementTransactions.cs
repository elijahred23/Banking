using System.Data;
using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

internal static class StatementTransactions
{
    public static async Task<T> Run<T>(IDbContextFactory<BankingDbContext> factory, Func<BankingDbContext, Task<T>> work, CancellationToken ct)
    {
        await using var probe = await factory.CreateDbContextAsync(ct);
        return await probe.Database.CreateExecutionStrategy().ExecuteAsync(async () => {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var result = await work(db);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return result;
        });
    }
    public static async Task<CorrespondentAccount> Account(BankingDbContext db, Guid bankId, Guid id, bool issuerOnly, CancellationToken ct) =>
        await db.CorrespondentAccounts.SingleOrDefaultAsync(x => x.Id == id
            && (x.IssuerBankId == bankId || !issuerOnly && x.RespondentBankId == bankId), ct)
        ?? throw new KeyNotFoundException("Correspondent account was not found at this bank.");
    public static Task<DateTimeOffset> Now(BankingDbContext db, CancellationToken ct) => db.SimulationStates
        .Where(x => x.Name == "default").Select(x => x.CurrentUtc).SingleAsync(ct);
}
