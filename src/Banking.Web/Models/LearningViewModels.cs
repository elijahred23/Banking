using Banking.Domain;

namespace Banking.Web.Models;

public sealed record LearningPathViewModel(
    string Id, string Title, string Audience, string Description, IReadOnlyList<string> MissionIds);

public sealed record LearningMissionViewModel(
    string Id,
    string PathId,
    string Title,
    string Summary,
    string Difficulty,
    int EstimatedMinutes,
    IReadOnlyList<string> Objectives,
    IReadOnlyList<string> Steps,
    string PredictionPrompt,
    IReadOnlyList<string> PredictionChoices,
    string Debrief,
    string ActionController,
    string ActionName,
    string ActionLabel,
    PaymentRail? Rail = null,
    ProcessingScenario? Scenario = null);

public sealed record LearningCenterViewModel(
    IReadOnlyList<LearningPathViewModel> Paths,
    IReadOnlyList<LearningMissionViewModel> Missions,
    IReadOnlySet<string> CompletedMissionIds);

public sealed record MissionPageViewModel(
    LearningMissionViewModel Mission,
    LearningPathViewModel Path,
    bool IsComplete,
    string? Prediction,
    IReadOnlyList<string> Hints,
    IReadOnlyList<string> RoleHandoffs,
    KnowledgeQuestionViewModel Question,
    LearningAttempt? LastAttempt,
    string ProductionNote);

public sealed record KnowledgeQuestionViewModel(string Id, string Prompt,
    IReadOnlyList<string> Choices, string CorrectAnswer, string Explanation);
public sealed record DiagnosticChallengeViewModel(string Id, string Title, string Evidence,
    IReadOnlyList<string> Suspects, string Diagnosis, string Verification);
public sealed record InstructorBankViewModel(string Bank, int Started, int Completed,
    int Correct, int Answered, DateTimeOffset? LastActivity);
public sealed record InstructorDashboardViewModel(IReadOnlyList<InstructorBankViewModel> Banks,
    IReadOnlyList<LearningAttempt> RecentAttempts, IReadOnlyList<LearningMissionViewModel> Missions);

public sealed record RailComparisonViewModel(IReadOnlyList<RailProfileViewModel> Rails);

public sealed record RailProfileViewModel(
    string Rail, string BestFor, string Speed, string Finality, string Limit,
    string Format, string Settlement, string KeyLesson);

public sealed record IsoExplorerViewModel(
    IReadOnlyList<IsoExplorerMessageViewModel> Messages,
    IReadOnlyList<IsoFieldDefinitionViewModel> Fields,
    IReadOnlyList<MessageTypeComparisonViewModel> MessageTypes);

public sealed record IsoExplorerMessageViewModel(
    Guid Id, Guid WireId, string Type, MessageDirection Direction, string Xml, DateTimeOffset CreatedDate);

public sealed record IsoFieldDefinitionViewModel(string Element, string Name, string Meaning);

public sealed record MessageTypeComparisonViewModel(
    string Type, string Purpose, string SentBy, string TypicalResponse, string ValueMovement);

public sealed record LearningDebriefViewModel(
    string Outcome, string Explanation, string ProductionNote, IReadOnlyList<string> Checkpoints);
