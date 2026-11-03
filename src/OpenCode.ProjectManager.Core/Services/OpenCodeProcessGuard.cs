using System.Diagnostics;
using OpenCode.ProjectManager.Core.Models;

namespace OpenCode.ProjectManager.Core.Services;

/// <summary>
/// Detects common OpenCode executable names. The database must not be modified while
/// OpenCode is running because its in-memory state can overwrite external changes.
/// </summary>
public static class OpenCodeProcessGuard
{
    private static readonly HashSet<string> KnownProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "opencode",
        "opencode-cli",
        "opencode-desktop",
        "opencode-service",
        "opencode-tui",
        "opencode2",
        "opencode2-node"
    };

    public static IReadOnlyList<RunningProcessInfo> FindRunningProcesses()
    {
        var currentId = Environment.ProcessId;
        var results = new List<RunningProcessInfo>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == currentId)
                {
                    continue;
                }

                string name;
                try
                {
                    name = Normalize(process.ProcessName);
                }
                catch (Exception)
                {
                    // The process can exit between enumeration and inspection.
                    continue;
                }

                if (!KnownProcessNames.Contains(name) &&
                    !name.StartsWith("opencode-", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("opencode2-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? executablePath = null;
                try
                {
                    executablePath = process.MainModule?.FileName;
                }
                catch (Exception)
                {
                    // Access can be denied for processes owned by another user.
                }

                results.Add(new RunningProcessInfo(process.Id, name, executablePath));
            }
        }

        return results
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ProcessId)
            .ToArray();
    }

    private static string Normalize(string processName)
    {
        var name = processName.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
    }
}



