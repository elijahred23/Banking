namespace Banking.Web.Models;

public sealed record WireInstructionHeadingViewModel(
    string Name,
    string Description,
    string Rule,
    bool IsAvailable);
