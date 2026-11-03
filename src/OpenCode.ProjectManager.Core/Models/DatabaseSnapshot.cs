namespace OpenCode.ProjectManager.Core.Models;

public sealed record DatabaseSnapshot(
    string DatabasePath,
    bool HasProjectTable,
    bool HasSessionV2Table,
    bool HasLegacySessionTable,
    bool HasArchivedColumn,
    IReadOnlyList<ProjectRecord> Projects,
    string? SchemaWarning = null)
{
    public bool IsSupported => HasProjectTable && HasSessionV2Table;

    public long FileSizeBytes { get; init; }

    public DateTimeOffset? LastWriteTime { get; init; }
}
