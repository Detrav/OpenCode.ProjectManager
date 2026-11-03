namespace OpenCode.ProjectManager.Core.Models;

public sealed record GlobalSessionDetails(
    GlobalSessionRecord Session,
    int MessageCount,
    int LegacyMessageCount,
    int PartCount,
    int ScheduleCount,
    int JobCount,
    int BlobReferenceCount,
    int EventSequenceCount,
    int ChildSessionCount)
{
    public int TotalMessageCount => MessageCount + LegacyMessageCount;

    public string MessagesDisplay => TotalMessageCount.ToString("N0");

    public string PartsDisplay => PartCount.ToString("N0");

    public string SchedulesDisplay => ScheduleCount.ToString("N0");

    public string JobsDisplay => JobCount.ToString("N0");

    public string BlobReferencesDisplay => BlobReferenceCount.ToString("N0");

    public string EventSequencesDisplay => EventSequenceCount.ToString("N0");

    public string ChildSessionsDisplay => ChildSessionCount.ToString("N0");
}
