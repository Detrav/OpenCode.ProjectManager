using OpenCode.ProjectManager.Core.Models;

namespace OpenCode.ProjectManager.Core.Services;

/// <summary>
/// Locates the OpenCode 2 SQLite database using the same XDG rules as the CLI.
/// </summary>
public static class DatabaseLocator
{
    public static IReadOnlyList<DatabaseCandidate> Discover()
    {
        var candidates = new List<DatabaseCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path, string source)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return;
            }

            if (!seen.Add(fullPath))
            {
                return;
            }

            try
            {
                var info = new FileInfo(fullPath);
                candidates.Add(new DatabaseCandidate(
                    fullPath,
                    source,
                    info.Exists,
                    info.Exists ? info.Length : 0,
                    info.Exists ? info.LastWriteTimeUtc : null));
            }
            catch (Exception)
            {
                candidates.Add(new DatabaseCandidate(fullPath, source, false, 0, null));
            }
        }

        var dataRoots = GetDataRoots();

        var configured = Environment.GetEnvironmentVariable("OPENCODE_DB");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // The CLI resolves a relative OPENCODE_DB against the XDG data/opencode directory.
            Add(Path.IsPathRooted(configured)
                    ? configured
                    : Path.Combine(GetPrimaryDataRoot(), configured),
                "OPENCODE_DB");
        }

        foreach (var root in dataRoots)
        {
            Add(Path.Combine(root, "opencode.db"), root);

            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(root, "opencode*.db", SearchOption.TopDirectoryOnly))
                {
                    Add(file, root);
                }
            }
            catch (Exception)
            {
                // An unreadable directory is not fatal: the user can still browse manually.
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Exists)
            .ThenBy(candidate => candidate.Source == "OPENCODE_DB" ? 0 : 1)
            .ThenByDescending(candidate => candidate.LastWriteTime)
            .ToArray();
    }

    public static IReadOnlyList<string> GetDataRoots()
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRoot(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                if (seen.Add(fullPath))
                {
                    roots.Add(fullPath);
                }
            }
            catch (Exception)
            {
                // Ignore malformed environment values.
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

        if (!string.IsNullOrWhiteSpace(xdgDataHome))
        {
            AddRoot(Path.Combine(xdgDataHome, "opencode"));
        }

        if (!string.IsNullOrWhiteSpace(home))
        {
            AddRoot(Path.Combine(home, ".local", "share", "opencode"));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            AddRoot(Path.Combine(localAppData, "opencode"));
        }

        return roots;
    }

    public static string GetPrimaryDataRoot() =>
        GetDataRoots().FirstOrDefault() ??
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local",
            "share",
            "opencode");

    public static string GetDefaultBackupDirectory(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        return Path.Combine(directory ?? Environment.CurrentDirectory, "opencode-project-manager-backups");
    }
}
