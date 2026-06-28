using Banking.Infrastructure.Checks;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class MicrParserTests
{
    [Fact]
    public void ParsesTeachingMicrLine()
    {
        var result = MicrParser.Parse("t101000019t 123456789o 0001234");
        Assert.Equal("101000019", result.RoutingNumber);
        Assert.Equal("123456789", result.AccountNumber);
        Assert.Equal("0001234", result.CheckNumber);
    }

    [Fact]
    public void ParsesMicrControlCharacters()
    {
        var result = MicrParser.Parse("⑆101000019⑆ 123456789⑈ 0001234");
        Assert.Equal("t101000019t 123456789o 0001234", result.NormalizedLine);
    }

    [Fact]
    public void RejectsBadRoutingChecksum() => Assert.Throws<ArgumentException>(() =>
        MicrParser.Parse("t101000010t 123456789o 0001234"));
}
