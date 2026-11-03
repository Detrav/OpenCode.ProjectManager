namespace OpenCode.ProjectManager.Core.Models;

public sealed record WorktreeRecord(string Directory, string? Strategy)
{
    public string DirectoryDisplay => ProjectRecord.ToDisplayPath(Directory);

    public string StrategyDisplay => string.IsNullOrWhiteSpace(Strategy) ? "main / plain" : Strategy;
}

public sealed record ProjectDirectoryRecord(string Directory, string? Type, string? Strategy)
{
    public string DirectoryDisplay => ProjectRecord.ToDisplayPath(Directory);

    public string TypeDisplay => string.IsNullOrWhiteSpace(Type) ? "—" : Type;
}

public sealed record ProjectDetails(
    ProjectRecord Project,
    IReadOnlyList<WorktreeRecord> Worktrees,
    IReadOnlyList<ProjectDirectoryRecord> LegacyDirectories,
    int MessageCount,
    int LegacyMessageCount,
    int PermissionCount,
    int ScheduleCount,
    int JobCount,
    int BlobReferenceCount,
    int EventSequenceCount)
{
    public int TotalMessageCount => MessageCount + LegacyMessageCount;

    public string MessagesDisplay => TotalMessageCount.ToString("N0");

    public string PermissionsDisplay => PermissionCount.ToString("N0");

    public string SchedulesDisplay => ScheduleCount.ToString("N0");

    public string JobsDisplay => JobCount.ToString("N0");

    public string BlobReferencesDisplay => BlobReferenceCount.ToString("N0");

    public string EventSequencesDisplay => EventSequenceCount.ToString("N0");

    public bool HasWorktrees => Worktrees.Count > 0;

    public bool HasLegacyDirectories => LegacyDirectories.Count > 0;

    public bool HasSandboxes => Project.Sandboxes.Count > 0;

    public bool HasStartupCommand => !string.IsNullOrWhiteSpace(Project.StartupCommand);

    public string SandboxesDisplay => Project.Sandboxes.Count == 0
        ? "—"
        : string.Join(Environment.NewLine, Project.Sandboxes.Select(ProjectRecord.ToDisplayPath));

    public string StartupCommandDisplay => string.IsNullOrWhiteSpace(Project.StartupCommand)
        ? "—"
        : Project.StartupCommand;
}
