namespace OpenCode.ProjectManager.Core.Models;

/// <summary>
/// A database file that may contain an OpenCode 2 installation.
/// </summary>
public sealed record DatabaseCandidate(
    string Path,
    string Source,
    bool Exists,
    long SizeBytes,
    DateTimeOffset? LastWriteTime)
{
    public string SizeDisplay => Exists ? FormatBytes(SizeBytes) : "not found";

    public string DisplayName
    {
        get
        {
            var fileName = System.IO.Path.GetFileName(Path);
            return Exists ? $"{fileName}  ·  {SizeDisplay}" : $"{fileName}  ·  missing";
        }
    }

    public string DisplayPath => Path;

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:0} {units[unit]}"
            : $"{value:0.##} {units[unit]}";
    }
}
