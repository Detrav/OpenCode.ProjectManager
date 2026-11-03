using System.Text.Json;

namespace OpenCode.ProjectManager.Core.Models;

/// <summary>
/// A single row from the OpenCode 2 <c>project</c> table together with cheap aggregate counts.
/// </summary>
public sealed record ProjectRecord
{
    public required string Id { get; init; }

    public required string CanonicalPath { get; init; }

    public string? Name { get; init; }

    public string? Vcs { get; init; }

    public long CreatedAt { get; init; }

    public long UpdatedAt { get; init; }

    public long? ActiveAt { get; init; }

    public long? ArchivedAt { get; init; }

    public int SessionCount { get; init; }

    public int LegacySessionCount { get; init; }

    public int WorktreeCount { get; init; }

    public int LegacyDirectoryCount { get; init; }

    public bool DirectoryExists { get; init; }

    public IReadOnlyList<string> Sandboxes { get; init; } = [];

    public string? StartupCommand { get; init; }

    public bool IsGlobal => string.Equals(Id, "global", StringComparison.Ordinal);

    public bool IsArchived => ArchivedAt is not null;

    public bool IsFolderMissing => !DirectoryExists && !IsGlobal;

    public int TotalSessionCount => SessionCount + LegacySessionCount;

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name))
            {
                return Name;
            }

            if (IsGlobal)
            {
                return "Global project";
            }

            var folder = System.IO.Path.GetFileName(CanonicalPath.TrimEnd('/', '\\'));
            return string.IsNullOrWhiteSpace(folder) ? CanonicalPath : folder;
        }
    }

    public string PathDisplay => IsGlobal ? "— (global sessions)" : ToDisplayPath(CanonicalPath);

    public string VcsDisplay => string.IsNullOrWhiteSpace(Vcs) ? "—" : Vcs;

    public string SessionsDisplay => $"{TotalSessionCount} session{(TotalSessionCount == 1 ? string.Empty : "s")}";

    public string WorktreesDisplay => $"{WorktreeCount} worktree{(WorktreeCount == 1 ? string.Empty : "s")}";

    public string StateDisplay
    {
        get
        {
            if (IsGlobal)
            {
                return "global";
            }

            if (!DirectoryExists)
            {
                return "folder missing";
            }

            return IsArchived ? "archived" : "active";
        }
    }

    public string LastActiveDisplay => FormatTimestamp(ActiveAt ?? UpdatedAt);

    public string CreatedDisplay => FormatTimestamp(CreatedAt);

    public string ArchivedDisplay => IsArchived ? FormatTimestamp(ArchivedAt) : "no";

    public override string ToString() => DisplayName;

    public static string ToDisplayPath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return path.Replace('/', '\\');
        }

        return path;
    }

    public static string FormatTimestamp(long? unixMilliseconds)
    {
        if (unixMilliseconds is null || unixMilliseconds <= 0)
        {
            return "—";
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds.Value)
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm");
        }
        catch (ArgumentOutOfRangeException)
        {
            return "—";
        }
    }

    public static IReadOnlyList<string> ParseSandboxes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string? ParseStartupCommand(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("start", out var start) &&
                   start.ValueKind == JsonValueKind.String
                ? start.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
