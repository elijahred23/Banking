namespace Banking.Infrastructure;

/// <summary>
/// Dedicated boundary for the learning lab's idempotent startup upgrade. Production-style
/// deployments should replace this implementation with managed EF migrations.
/// </summary>
public sealed class LabSchemaUpgradeRunner
{
    public Task UpgradeAsync(BankingDbContext db, CancellationToken token = default) =>
        ServiceRegistration.ApplyLabSchemaUpgradeAsync(db, token);
}
