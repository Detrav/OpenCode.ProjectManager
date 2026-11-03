namespace OpenCode.ProjectManager.Core.Models;

public enum ProjectListItemKind
{
    Project,
    GlobalSession
}

/// <summary>
/// A row in the left-hand list. Projects and global sessions share the same shape
/// so the user sees and deletes them the same way.
/// </summary>
public sealed record ProjectListItem
{
    public required ProjectListItemKind Kind { get; init; }

    public ProjectRecord? Project { get; init; }

    public GlobalSessionRecord? GlobalSession { get; init; }

    public bool IsProject => Kind == ProjectListItemKind.Project;

    public bool IsGlobalSession => Kind == ProjectListItemKind.GlobalSession;

    public string Id => Project?.Id ?? GlobalSession?.Id ?? string.Empty;

    public string DisplayName => Project?.DisplayName ?? GlobalSession?.DisplayName ?? string.Empty;

    public string PathDisplay => Project?.PathDisplay ?? GlobalSession?.PathDisplay ?? string.Empty;

    public string StateDisplay => Project?.StateDisplay ?? GlobalSession?.StateDisplay ?? string.Empty;

    public string SessionsDisplay => Project?.SessionsDisplay ?? GlobalSession?.SessionsDisplay ?? string.Empty;

    public string? Vcs => Project?.Vcs;

    public long SortTimestamp => Project?.ActiveAt ?? Project?.UpdatedAt ?? GlobalSession?.UpdatedAt ?? 0;

    public static ProjectListItem FromProject(ProjectRecord project) =>
        new()
        {
            Kind = ProjectListItemKind.Project,
            Project = project
        };

    public static ProjectListItem FromGlobalSession(GlobalSessionRecord session) =>
        new()
        {
            Kind = ProjectListItemKind.GlobalSession,
            GlobalSession = session
        };

    public override string ToString() => DisplayName;
}
