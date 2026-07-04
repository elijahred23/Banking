using Banking.Web;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class LearningExperienceTests
{
    [Fact]
    public void EveryMissionBelongsToAPathAndHasAnAnswerInItsChoices()
    {
        foreach (var mission in LearningCatalog.Missions)
        {
            Assert.Contains(LearningCatalog.Paths, path => path.MissionIds.Contains(mission.Id));
            var question = LearningCatalog.Question(mission.Id);
            Assert.Contains(question.CorrectAnswer, question.Choices);
            Assert.NotEmpty(mission.Objectives);
            Assert.NotEmpty(mission.Steps);
            Assert.False(string.IsNullOrWhiteSpace(LearningCatalog.ProductionNote(mission.Id)));
        }
    }

    [Fact]
    public void CurriculumCoversEverySimulatedRailAndNonValueMessages()
    {
        var ids = LearningCatalog.Missions.Select(x => x.Id).ToHashSet();
        Assert.Contains("first-fedwire", ids);
        Assert.Contains("fednow-finality", ids);
        Assert.Contains("swift-route", ids);
        Assert.Contains("ach-file", ids);
        Assert.Contains("check-clearing", ids);
        Assert.Contains("nonvalue-request", ids);
    }

    [Fact]
    public void FailureMissionsTeachNoPrematureValueMovement()
    {
        Assert.Contains("settlement journal", LearningCatalog.Mission("rejected-wire")!.Steps.Last(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prevent a second settlement", LearningCatalog.Mission("check-duplicate")!.Debrief,
            StringComparison.OrdinalIgnoreCase);
    }
}
