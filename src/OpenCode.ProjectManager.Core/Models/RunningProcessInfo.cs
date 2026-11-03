namespace OpenCode.ProjectManager.Core.Models;

public sealed record RunningProcessInfo(int ProcessId, string Name, string? ExecutablePath)
{
    public string Display => $"{Name} (PID {ProcessId})";
}
