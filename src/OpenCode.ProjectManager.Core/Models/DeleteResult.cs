namespace OpenCode.ProjectManager.Core.Models;

public sealed record DeleteResult(
    string ProjectId,
    string ProjectName,
    int SessionsV2,
    int SessionsV1,
    int Worktrees,
    int LegacyDirectories,
    int Messages,
    int EventSequences,
    int Jobs,
    int OrphanedBlobs,
    string? BackupPath)
{
    public int TotalSessions => SessionsV2 + SessionsV1;
}
