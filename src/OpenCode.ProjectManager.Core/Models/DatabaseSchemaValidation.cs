namespace OpenCode.ProjectManager.Core.Models;

/// <summary>
/// Result of checking whether a database matches the OpenCode 2 schema this tool expects.
/// </summary>
public sealed record DatabaseSchemaValidation(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsCompatible => Errors.Count == 0;

    public string ErrorSummary => string.Join(Environment.NewLine, Errors);

    public string WarningSummary => string.Join(Environment.NewLine, Warnings);
}
