namespace Banking.Infrastructure;

/// <summary>
/// Dedicated boundary for the learning lab's idempotent startup upgrade. Production-style
/// deployments should replace this implementation with managed EF migrations.
/// </summary>
public sealed class LabSchemaUpgradeRunner
{
    public async Task UpgradeAsync(BankingDbContext db, CancellationToken token = default)
    {
        await ServiceRegistration.ApplyLabSchemaUpgradeAsync(db, token);
        using var stream = typeof(LabSchemaUpgradeRunner).Assembly.GetManifestResourceStream("StudioSupport.sql")!;
        using var reader = new StreamReader(stream);
        await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(
            db.Database, await reader.ReadToEndAsync(token), token);
    }
}
