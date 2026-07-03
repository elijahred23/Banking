using System.ComponentModel.DataAnnotations;
namespace Banking.Domain;
public sealed class LearningProgress
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid BankId { get; set; }
    public Bank Bank { get; set; } = null!; [MaxLength(40)] public required string MissionId { get; set; }
    [MaxLength(200)] public string? Prediction { get; set; } public bool IsComplete { get; set; }
    public int CorrectAnswers { get; set; } public int AnsweredQuestions { get; set; }
    public DateTimeOffset UpdatedDate { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class LearningAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid BankId { get; set; }
    public Bank Bank { get; set; } = null!; [MaxLength(40)] public required string MissionId { get; set; }
    [MaxLength(40)] public required string QuestionId { get; set; }
    [MaxLength(200)] public required string SelectedAnswer { get; set; } public bool IsCorrect { get; set; }
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
}
