using Banking.Domain;
using Xunit;

namespace Banking.Domain.Tests;

public class StudioPracticeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Practice_preserves_funds_and_separates_delivery_from_posting(bool ach)
    {
        var cases = StudioPractice.Cases(ach);
        Assert.Equal(cases.Count, cases.Select(x => x.Id).Distinct().Count());
        foreach (var scenario in cases)
        {
            foreach (var step in scenario.Steps)
            {
                Assert.InRange(step.Answer, 0, step.Choices.Length - 1);
                Assert.InRange(step.Held, 0, StudioPractice.Amount);
                Assert.InRange(step.Posted, 0, StudioPractice.Amount);
                Assert.True(StudioPractice.OpeningBalance - step.Posted - step.Held >= 0);
                Assert.False(step.Held > 0 && step.Posted > 0);
            }
            var delivery = scenario.Steps.FirstOrDefault(x => x.Title == "Transport delivered");
            if (delivery is not null)
            {
                Assert.Equal(0, delivery.Posted);
                Assert.Equal(StudioPractice.Amount, delivery.Held);
            }
        }
        Assert.Equal(0, cases.Single(x => x.Id == "rejected").Steps[^1].Held);
        Assert.Equal(0, cases.Single(x => x.Id == "rejected").Steps[^1].Posted);
        Assert.Equal(StudioPractice.Amount, cases.Single(x => x.Id == "duplicate").Steps[^1].Posted);
    }

    [Fact]
    public void Return_reverses_value_but_notification_and_cancellation_do_not()
    {
        Assert.Equal(0, StudioPractice.Cases(true).Single(x => x.Id == "return").Steps[^1].Posted);
        Assert.Equal(StudioPractice.Amount, StudioPractice.Cases(true).Single(x => x.Id == "noc").Steps[^1].Posted);
        Assert.Equal(StudioPractice.Amount, StudioPractice.Cases(false).Single(x => x.Id == "recall").Steps[^1].Posted);
        Assert.Equal(0, StudioPractice.Cases(false).Single(x => x.Id == "compliance").Steps[^1].Posted);
    }
}
