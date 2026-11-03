namespace OpenCode.ProjectManager.Core.Models;

/// <summary>
/// A session that belongs to the built-in global project. These are shown in the
/// list as standalone entries because the global project row itself is not displayed.
/// </summary>
public sealed record GlobalSessionRecord
{
    public required string Id { get; init; }

    public required string ProjectId { get; init; }

    public string? ParentId { get; init; }

    public required string Slug { get; init; }

    public string? Title { get; init; }

    public required string Directory { get; init; }

    public string? Agent { get; init; }

    public string? Model { get; init; }

    public long CreatedAt { get; init; }

    public long UpdatedAt { get; init; }

    public long? ArchivedAt { get; init; }

    public int MessageCount { get; init; }

    public int LegacyMessageCount { get; init; }

    public bool DirectoryExists { get; init; }

    public bool IsArchived => ArchivedAt is not null;

    public bool IsFolderMissing => !DirectoryExists;

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title))
            {
                return Title;
            }

            if (!string.IsNullOrWhiteSpace(Slug))
            {
                return Slug;
            }

            var folder = Path.GetFileName(Directory.TrimEnd('/', '\\'));
            return string.IsNullOrWhiteSpace(folder) ? Id : folder;
        }
    }

    public string PathDisplay => ProjectRecord.ToDisplayPath(Directory);

    public string StateDisplay => IsArchived
        ? "archived"
        : DirectoryExists
            ? "active"
            : "folder missing";

    public string SessionsDisplay => "1 session";

    public string CreatedDisplay => ProjectRecord.FormatTimestamp(CreatedAt);

    public string LastActiveDisplay => ProjectRecord.FormatTimestamp(UpdatedAt);

    public string ArchivedDisplay => IsArchived ? ProjectRecord.FormatTimestamp(ArchivedAt) : "no";

    public string AgentDisplay => string.IsNullOrWhiteSpace(Agent) ? "—" : Agent;

    public string ModelDisplay => string.IsNullOrWhiteSpace(Model) ? "—" : Model;

    public override string ToString() => DisplayName;
}
