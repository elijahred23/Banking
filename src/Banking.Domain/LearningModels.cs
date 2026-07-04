using System.ComponentModel.DataAnnotations;
namespace Banking.Domain;
public sealed class LearningProgress
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid BankId { get; set; }
    public Bank Bank { get; set; } = null!; [MaxLength(40)] public required string MissionId { get; set; }
    [MaxLength(80)] public string LearnerAlias { get; set; } = "Bank team";
    [MaxLength(200)] public string? Prediction { get; set; } public bool IsComplete { get; set; }
    public int CorrectAnswers { get; set; } public int AnsweredQuestions { get; set; }
    public DateTimeOffset? NextReviewDate { get; set; }
    public DateTimeOffset UpdatedDate { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class LearningAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid BankId { get; set; }
    public Bank Bank { get; set; } = null!; [MaxLength(40)] public required string MissionId { get; set; }
    [MaxLength(40)] public required string QuestionId { get; set; }
    [MaxLength(200)] public required string SelectedAnswer { get; set; } public bool IsCorrect { get; set; }
    [MaxLength(1000)] public string? LearnerExplanation { get; set; }
    public int ExplanationScore { get; set; }
    [MaxLength(80)] public string LearnerAlias { get; set; } = "Bank team";
    public int Confidence { get; set; } = 3;
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
}

public static class LearningExplanationRubric
{
    private static readonly string[] EvidenceTerms =
        ["transport", "message", "status", "settlement", "journal", "hold", "balance", "return", "correlation"];

    public static int Score(string? explanation)
    {
        if (string.IsNullOrWhiteSpace(explanation) || explanation.Trim().Length < 20) return 0;
        var normalized = explanation.ToLowerInvariant();
        var concepts = EvidenceTerms.Count(normalized.Contains);
        return concepts >= 3 ? 3 : concepts == 2 ? 2 : 1;
    }
}

public sealed class LearningRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    public Bank Bank { get; set; } = null!;
    [MaxLength(40)] public required string MissionId { get; set; }
    [MaxLength(80)] public string LearnerAlias { get; set; } = "Bank team";
    [MaxLength(30)] public required string Rail { get; set; }
    [MaxLength(20)] public string? ItemType { get; set; }
    public Guid? ItemId { get; set; }
    [MaxLength(200)] public string? Prediction { get; set; }
    public decimal BaselineCustomerBalance { get; set; }
    public decimal BaselineHeldBalance { get; set; }
    public decimal BaselineMasterBalance { get; set; }
    [MaxLength(20)] public string Status { get; set; } = "Started";
    [MaxLength(2000)] public string? EvidenceSummary { get; set; }
    public int EvidenceScore { get; set; }
    public DateTimeOffset StartedDate { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedDate { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedDate { get; set; }
}

public sealed class LearningAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    public Bank Bank { get; set; } = null!;
    [MaxLength(40)] public required string MissionId { get; set; }
    [MaxLength(80)] public required string Cohort { get; set; }
    [MaxLength(120)] public required string AssignedBy { get; set; }
    public DateTimeOffset? DueDate { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
}
