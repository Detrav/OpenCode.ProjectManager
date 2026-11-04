namespace OpenCode.ProjectManager.Core.Models;

/// <summary>
/// SQLite page usage for a database file.
/// Free pages were released by deletes or rewrites but are still part of the
/// file until it is rebuilt with VACUUM, so they can be reclaimed.
/// </summary>
public sealed record DatabaseSpaceInfo(
    long FileSizeBytes,
    long PageSize,
    long PageCount,
    long FreePageCount)
{
    public long LiveBytes => Math.Max(0, PageCount - FreePageCount) * Math.Max(0, PageSize);

    public long ReclaimableBytes => Math.Max(0, FreePageCount) * Math.Max(0, PageSize);

    public double FreePercent => PageCount <= 0
        ? 0d
        : Math.Max(0, FreePageCount) * 100d / PageCount;
}
