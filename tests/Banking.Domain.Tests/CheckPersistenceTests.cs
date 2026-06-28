using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class CheckPersistenceTests
{
    [Fact]
    public void SqlServerModelGeneratesCheckSchema()
    {
        var options = new DbContextOptionsBuilder<BankingDbContext>()
            .UseSqlServer("Server=localhost;Database=unused;User Id=unused;Password=unused;TrustServerCertificate=True")
            .Options;
        using var db = new BankingDbContext(options);
        var script = db.Database.GenerateCreateScript();
        Assert.Contains("CREATE TABLE [CheckDeposits]", script);
        Assert.Contains("IX_CheckImages_CheckDepositId_Side", script);
        Assert.Contains("[Side] nvarchar(10)", script);
    }
}
